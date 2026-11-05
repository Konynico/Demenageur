using System.Text;
using Demenageur.Core.Journal;
using Demenageur.Core.Moving;
using Demenageur.Tests.Infrastructure;

namespace Demenageur.Tests.Moving;

public sealed class JournalTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("100 % sûr")]
    [InlineData("iso\uD800le")]
    [InlineData("fin\uDC00")]
    [InlineData("%u0041 littéral")]
    [InlineData("😀 paire valide")]
    public void Lossless_text_round_trips(string text)
    {
        var encoded = LosslessText.Encode(text);
        Assert.Equal(text, LosslessText.Decode(encoded));
        Assert.DoesNotContain(encoded, c => char.IsSurrogate(c) && !encoded.Contains("😀", StringComparison.Ordinal));
    }

    private static (string Path, byte[] Bytes) WriteJournal(TestSandbox sandbox, int records)
    {
        Directory.CreateDirectory(sandbox.PathOf("ops"));
        var path = sandbox.PathOf("ops", "journal.jsonl");
        var journal = new OperationJournal(sandbox.Guarded(sandbox.PathOf("ops")), path);
        for (var i = 0; i < records; i++)
        {
            journal.Append("Etape" + i, "message é\uD800", new Dictionary<string, string> { ["chemin"] = @"C:\Program Files\App " + i });
        }

        return (path, File.ReadAllBytes(path));
    }

    [Fact]
    public void Journal_round_trips_records_and_values()
    {
        using var sandbox = new TestSandbox();
        var (_, bytes) = WriteJournal(sandbox, 3);
        var content = OperationJournal.Parse(bytes);
        Assert.False(content.Corrupt);
        Assert.False(content.TornTail);
        Assert.Equal(3, content.Records.Count);
        Assert.Equal("message é\uD800", content.Records[2].Message);
        Assert.Equal(@"C:\Program Files\App 2", content.Records[2].Get("chemin"));
        Assert.Equal([1, 2, 3], content.Records.Select(r => r.Sequence));
    }

    [Fact]
    public void A_torn_last_record_is_ignored_at_every_cut_position()
    {
        using var sandbox = new TestSandbox();
        var (_, bytes) = WriteJournal(sandbox, 3);
        var lastLineStart = Array.LastIndexOf(bytes, (byte)'\n', bytes.Length - 2) + 1;
        for (var cut = lastLineStart + 1; cut < bytes.Length; cut++)
        {
            var content = OperationJournal.Parse(bytes[..cut]);
            Assert.False(content.Corrupt, $"Coupure à l'octet {cut} prise pour une corruption.");
            Assert.True(content.TornTail, $"Coupure à l'octet {cut} non détectée.");
            Assert.Equal(2, content.Records.Count);
        }
    }

    [Fact]
    public void A_damaged_record_in_the_middle_marks_the_journal_corrupt()
    {
        using var sandbox = new TestSandbox();
        var (_, bytes) = WriteJournal(sandbox, 3);
        var secondLine = Array.IndexOf(bytes, (byte)'\n') + 1;
        var damaged = (byte[])bytes.Clone();
        damaged[secondLine + 20] ^= 0x01;
        var content = OperationJournal.Parse(damaged);
        Assert.True(content.Corrupt);
        Assert.Single(content.Records);
    }

    [Fact]
    public void Recovery_never_acts_on_a_corrupt_journal()
    {
        using var scenario = new MoveScenario(minimal: true);
        scenario.Faulty.FailAt.Add(15);
        scenario.Move();
        scenario.Faulty.FailAt.Clear();

        // On simule un « Locked » laissé en suspens, puis on abîme une ligne du milieu.
        var operation = scenario.Store().List().Single();
        var journalPath = Path.Combine(operation.Directory, OperationStore.JournalFileName);
        var bytes = File.ReadAllBytes(journalPath);
        var secondLine = Array.IndexOf(bytes, (byte)'\n') + 1;
        bytes[secondLine + 15] ^= 0x01;
        File.WriteAllBytes(journalPath, bytes);
        var before = Directory.GetFileSystemEntries(scenario.Sandbox.Root, "*", SearchOption.AllDirectories).Length;

        var results = scenario.RecoverAll();

        var result = Assert.Single(results);
        Assert.Equal(OperationOutcome.NeedsAttention, result.Outcome);
        Assert.Equal(before, Directory.GetFileSystemEntries(scenario.Sandbox.Root, "*", SearchOption.AllDirectories).Length);
        Assert.Contains("corrompu", result.Message, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(bytes), File.ReadAllText(journalPath));
    }
}
