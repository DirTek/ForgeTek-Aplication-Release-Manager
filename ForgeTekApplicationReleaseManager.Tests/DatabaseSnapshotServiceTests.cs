using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using ForgeTekApplicationReleaseManager.Data;
using ForgeTekApplicationReleaseManager.Services;
using ForgeTekApplicationReleaseManager.Services.Storage;

namespace ForgeTekApplicationReleaseManager.Tests;

public class DatabaseSnapshotServiceTests
{
    private sealed class FileDbFactory(string path) : IDbContextFactory<ForgeTekDbContext>
    {
        public ForgeTekDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<ForgeTekDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    [Fact]
    public void TrySnapshot_WritesACopyThatContainsTheData()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dbPath = Path.Combine(dir, "forgetek.db");
            var factory = new FileDbFactory(dbPath);
            using (var db = factory.CreateDbContext())
            {
                db.Database.EnsureCreated();
                db.Apps.Add(new AppRow { Id = "1", Name = "STLVerse", Payload = "{}" });
                db.SaveChanges();
            }

            var snapshot = new DatabaseSnapshotService(factory, Substitute.For<ILogService>()).TrySnapshot("delete STLVerse");

            Assert.NotNull(snapshot);
            Assert.Equal(Path.Combine(dir, "snapshots"), Path.GetDirectoryName(snapshot));
            Assert.Contains("delete-STLVerse", Path.GetFileName(snapshot));
            using var copy = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False");
            copy.Open();
            using var cmd = copy.CreateCommand();
            cmd.CommandText = "select Name from Apps";
            Assert.Equal("STLVerse", cmd.ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void TrySnapshot_KeepsOnlyTheNewest()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var dbPath = Path.Combine(dir, "forgetek.db");
            var factory = new FileDbFactory(dbPath);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();

            var snapshots = Directory.CreateDirectory(Path.Combine(dir, "snapshots")).FullName;
            for (var i = 0; i < DatabaseSnapshotService.Keep + 5; i++)
                File.WriteAllText(Path.Combine(snapshots, $"forgetek-2000010{i % 10}-0000{i:D2}-old.db"), "");

            var newest = new DatabaseSnapshotService(factory, Substitute.For<ILogService>()).TrySnapshot("retract");

            var left = Directory.GetFiles(snapshots, "forgetek-*.db");
            Assert.Equal(DatabaseSnapshotService.Keep, left.Length);
            Assert.Contains(newest, left);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public void TrySnapshot_MissingDatabase_ReturnsNull_AndCreatesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid());
        var factory = new FileDbFactory(Path.Combine(dir, "x.db"));
        Assert.Null(new DatabaseSnapshotService(factory, Substitute.For<ILogService>()).TrySnapshot("delete"));
        Assert.False(Directory.Exists(dir));
    }
}
