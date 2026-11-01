using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Tests;

public sealed class JsonFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aviates-store-" + Guid.NewGuid().ToString("N"));

    public JsonFileStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Write_then_Read_round_trips_and_leaves_no_temp_file()
    {
        var store = new JsonFileStore(Path.Combine(_dir, "flights.json"), "Test");

        store.Write(() => new List<string> { "EGLL", "KJFK" });

        Assert.Equal(new[] { "EGLL", "KJFK" }, store.Read<List<string>>());
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Read_of_missing_file_returns_null()
    {
        var store = new JsonFileStore(Path.Combine(_dir, "missing.json"), "Test");

        Assert.Null(store.Read<List<string>>());
    }

    [Fact]
    public void Read_of_corrupt_file_keeps_a_copy_before_returning_null()
    {
        var path = Path.Combine(_dir, "flights.json");
        File.WriteAllText(path, "[{\"Id\": \"half-writ");
        var store = new JsonFileStore(path, "Test");

        Assert.Null(store.Read<List<FlightRecord>>());

        var kept = Assert.Single(Directory.GetFiles(_dir, "flights.json.corrupt-*"));
        Assert.Equal("[{\"Id\": \"half-writ", File.ReadAllText(kept));
    }

    [Fact]
    public void Concurrent_writes_end_with_the_latest_state_on_disk()
    {
        var store = new JsonFileStore(Path.Combine(_dir, "list.json"), "Test");
        var items = new List<int>();
        var gate = new object();

        Parallel.For(0, 50, i =>
        {
            lock (gate) items.Add(i);
            store.Write(() => { lock (gate) return items.ToList(); });
        });

        Assert.Equal(50, store.Read<List<int>>()!.Count);
    }
}

public class PilotMessageDuplicateTests
{
    private static PilotMessage Message(string content, DateTime sentAt) => new()
    {
        SenderId = "a", RecipientId = "b", Content = content, SentAt = sentAt,
    };

    [Fact]
    public void Same_message_under_a_different_id_is_the_same_message()
    {
        var local  = Message("Ready for pushback", new DateTime(2026, 10, 13, 10, 18, 26, DateTimeKind.Utc));
        var server = Message("Ready for pushback ", local.SentAt.AddSeconds(3));

        Assert.NotEqual(local.Id, server.Id);
        Assert.True(local.IsSameMessageAs(server));
    }

    [Fact]
    public void Same_text_sent_minutes_apart_is_a_different_message()
    {
        var first  = Message("ok", new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc));
        var second = Message("ok", first.SentAt.AddMinutes(5));

        Assert.False(first.IsSameMessageAs(second));
    }
}
