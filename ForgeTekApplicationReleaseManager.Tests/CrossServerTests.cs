using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using ForgeTekUpdatePackager.Data;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.Services.Security;
using ForgeTekUpdatePackager.Services.Storage;

namespace ForgeTekUpdatePackager.Tests;

/// <summary>A SQLite (in-memory, kept alive) DbContext factory for exercising the EF services without a server.</summary>
sealed class TestDbFactory : IDbContextFactory<ForgeTekDbContext>, IDisposable
{
    private readonly SqliteConnection _conn;

    public TestDbFactory()
    {
        _conn = new SqliteConnection("Filename=:memory:");
        _conn.Open();
        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public ForgeTekDbContext CreateDbContext()
    {
        var opts = new DbContextOptionsBuilder<ForgeTekDbContext>().UseSqlite(_conn).Options;
        return new ForgeTekDbContext(opts);
    }

    public void Dispose() => _conn.Dispose();
}

public class ApprovalStateTests
{
    private static ReleaseApproval Vote(ApprovalDecision d, UserRole role, int minute) => new()
    {
        TargetKey = "t", Decision = d, ByRole = role, ByUser = role.ToString(),
        TimestampUtc = new DateTime(2026, 1, 1, 0, minute, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void AdminAndQa_Approve_IsApproved()
    {
        var votes = new[] { Vote(ApprovalDecision.Approve, UserRole.Admin, 1),
                            Vote(ApprovalDecision.Approve, UserRole.QaTester, 2) };
        Assert.Equal(ApprovalState.Approved, ApprovalService.Evaluate(votes));
        Assert.Equal(2, ApprovalService.CountSatisfied(votes));
    }

    [Fact]
    public void OnlyAdmin_IsPending_OneOfTwo()
    {
        var votes = new[] { Vote(ApprovalDecision.Approve, UserRole.Admin, 1) };
        Assert.Equal(ApprovalState.Pending, ApprovalService.Evaluate(votes));
        Assert.Equal(1, ApprovalService.CountSatisfied(votes));
    }

    [Fact]
    public void RejectNewerThanApprovals_IsRejected()
    {
        var votes = new[] { Vote(ApprovalDecision.Approve, UserRole.Admin, 1),
                            Vote(ApprovalDecision.Approve, UserRole.QaTester, 2),
                            Vote(ApprovalDecision.Reject, UserRole.QaTester, 3) };
        Assert.Equal(ApprovalState.Rejected, ApprovalService.Evaluate(votes));
    }

    [Fact]
    public void Notes_DoNotCount()
    {
        var votes = new[] { Vote(ApprovalDecision.Note, UserRole.Admin, 1),
                            Vote(ApprovalDecision.Note, UserRole.QaTester, 2) };
        Assert.Equal(ApprovalState.Pending, ApprovalService.Evaluate(votes));
        Assert.Equal(0, ApprovalService.CountSatisfied(votes));
    }

    [Fact]
    public void ReApprovalAfterReject_RecountsFromTheReject()
    {
        var votes = new[] { Vote(ApprovalDecision.Approve, UserRole.Admin, 1),
                            Vote(ApprovalDecision.Approve, UserRole.QaTester, 2),
                            Vote(ApprovalDecision.Reject, UserRole.Admin, 3),
                            Vote(ApprovalDecision.Approve, UserRole.Admin, 4),
                            Vote(ApprovalDecision.Approve, UserRole.QaTester, 5) };
        Assert.Equal(ApprovalState.Approved, ApprovalService.Evaluate(votes));
    }
}

public class SecretProtectorTests
{
    [Fact]
    public void Dpapi_RoundTrips()
    {
        var p = new DpapiSecretProtector();
        var cipher = p.Protect("hunter2");
        Assert.NotEqual("hunter2", cipher);
        Assert.True(p.IsProtected(cipher));
        Assert.Equal("hunter2", p.Unprotect(cipher));
    }

    [Fact]
    public void Dpapi_PassesThroughEmpty()
    {
        var p = new DpapiSecretProtector();
        Assert.Equal(string.Empty, p.Protect(null));
        Assert.False(p.IsProtected("not-a-cipher"));
    }

    [Fact]
    public void NoSharedProtector_BlocksProtect_PassesReads()
    {
        var p = new NoSharedProtectorYet();
        Assert.Throws<NoSharedProtectorYet.SecretSharingNotConfiguredException>(() => p.Protect("secret"));
        Assert.Equal(string.Empty, p.Protect(""));      // empty is a no-op, not a block
        Assert.Equal("plain", p.Unprotect("plain"));    // reads pass through
        Assert.False(p.IsProtected("anything"));
    }
}

public class EfStorageServiceTests
{
    private static AppEntry SampleApp() => new()
    {
        Name = "TestApp", FolderPath = @"C:\apps\test",
        Versions =
        {
            new AppVersion { VersionNumber = "1.0", IsInitial = true, ScanDate = DateTime.Now },
            new AppVersion { VersionNumber = "1.1", ScanDate = DateTime.Now, FtpPassword = "s3cret" },
        },
    };

    [Fact]
    public void Add_Then_GetById_RoundTrips()
    {
        using var factory = new TestDbFactory();
        var svc = new EfStorageService(factory, new DpapiSecretProtector());
        var app = SampleApp();
        svc.Add(app);

        var loaded = svc.GetById(app.Id);
        Assert.NotNull(loaded);
        Assert.Equal("TestApp", loaded!.Name);
        Assert.Equal(2, loaded.Versions.Count);
        Assert.Equal("s3cret", loaded.Versions[1].FtpPassword);   // secret decrypted back
        Assert.Single(svc.GetAll());
    }

    [Fact]
    public void Networked_DropsSecrets_KeepsMetadata()
    {
        using var factory = new TestDbFactory();
        var svc = new EfStorageService(factory, new NoSharedProtectorYet());   // simulates networked mode
        var app = SampleApp();
        svc.Add(app);   // must not throw even though a version carries an FTP secret

        var loaded = svc.GetById(app.Id)!;
        Assert.Equal("TestApp", loaded.Name);                 // metadata preserved
        Assert.True(string.IsNullOrEmpty(loaded.Versions[1].FtpPassword));   // secret dropped
    }

    [Fact]
    public void ConcurrentEdits_ThrowConcurrencyException()
    {
        using var factory = new TestDbFactory();
        var svc = new EfStorageService(factory, new DpapiSecretProtector());
        var app = SampleApp();
        svc.Add(app);

        using var ctx1 = factory.CreateDbContext();
        using var ctx2 = factory.CreateDbContext();
        var row1 = ctx1.Apps.First(a => a.Id == app.Id);
        var row2 = ctx2.Apps.First(a => a.Id == app.Id);
        row1.Name = "Edited by A";
        row2.Name = "Edited by B";
        ctx1.SaveChanges();
        Assert.Throws<DbUpdateConcurrencyException>(() => ctx2.SaveChanges());
    }
}

public class EfUserServiceTests
{
    [Fact]
    public void Create_Authenticate_RoundTrips()
    {
        using var factory = new TestDbFactory();
        var svc = new EfUserService(factory);
        Assert.False(svc.HasAnyUsers);

        svc.Create("Alice", "pw123", UserRole.QaTester);
        Assert.True(svc.HasAnyUsers);
        Assert.NotNull(svc.Authenticate("alice", "pw123"));   // case-insensitive
        Assert.Null(svc.Authenticate("alice", "wrong"));
        Assert.Equal(UserRole.QaTester, svc.GetByName("Alice")!.Role);
    }

    [Fact]
    public void Create_Duplicate_Throws()
    {
        using var factory = new TestDbFactory();
        var svc = new EfUserService(factory);
        svc.Create("bob", "x", UserRole.Admin);
        Assert.Throws<InvalidOperationException>(() => svc.Create("BOB", "y", UserRole.Admin));
    }
}

public class FileBlobStoreTests
{
    private static string MakeTempFile(string dir, string relPath, byte[] content)
    {
        var full = Path.Combine(dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static FileRecord Record(string relPath, byte[] content) => new()
    {
        Path = relPath,
        Checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(),
    };

    [Fact]
    public async Task Store_Then_Get_RoundTrips_Compressible()
    {
        using var factory = new TestDbFactory();
        var store = new EfFileBlobStore(factory, new EfStorageService(factory, new DpapiSecretProtector()));
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var content = System.Text.Encoding.UTF8.GetBytes(new string('a', 5000));   // very compressible
            var rec = Record("sub/a.txt", content);
            MakeTempFile(dir, rec.Path, content);

            await store.StoreAsync(dir, new[] { rec });

            Assert.True(await store.HasAsync(rec.Checksum));
            Assert.Equal(content, await store.GetAsync(rec.Checksum));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Store_Dedups_By_Checksum()
    {
        using var factory = new TestDbFactory();
        var store = new EfFileBlobStore(factory, new EfStorageService(factory, new DpapiSecretProtector()));
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var content = System.Text.Encoding.UTF8.GetBytes("identical contents");
            var a = Record("a.txt", content);
            var b = Record("copy/a.txt", content);   // same bytes → same checksum
            MakeTempFile(dir, a.Path, content);
            MakeTempFile(dir, b.Path, content);

            await store.StoreAsync(dir, new[] { a });
            await store.StoreAsync(dir, new[] { a, b });   // re-store + a duplicate path

            using var db = factory.CreateDbContext();
            Assert.Equal(1, db.FileBlobs.Count());   // stored exactly once
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Store_Skips_Debug_And_Removed()
    {
        using var factory = new TestDbFactory();
        var store = new EfFileBlobStore(factory, new EfStorageService(factory, new DpapiSecretProtector()));
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var keep = Record("keep.dll", System.Text.Encoding.UTF8.GetBytes("keep"));
            var dbg  = Record("skip.pdb", System.Text.Encoding.UTF8.GetBytes("debug"));
            dbg.IsDebug = true;
            MakeTempFile(dir, keep.Path, System.Text.Encoding.UTF8.GetBytes("keep"));
            MakeTempFile(dir, dbg.Path, System.Text.Encoding.UTF8.GetBytes("debug"));

            await store.StoreAsync(dir, new[] { keep, dbg });

            Assert.True(await store.HasAsync(keep.Checksum));
            Assert.False(await store.HasAsync(dbg.Checksum));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CollectGarbage_DropsUnreferenced_KeepsLive()
    {
        using var factory = new TestDbFactory();
        var storage = new EfStorageService(factory, new DpapiSecretProtector());
        var store = new EfFileBlobStore(factory, storage);
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var liveContent = System.Text.Encoding.UTF8.GetBytes("still shipping");
            var live = Record("app.exe", liveContent);
            MakeTempFile(dir, live.Path, liveContent);
            await store.StoreAsync(dir, new[] { live });

            // An orphan blob not referenced by any version.
            using (var db = factory.CreateDbContext())
            {
                db.FileBlobs.Add(new ForgeTekUpdatePackager.Data.FileBlobRow
                { Sha256 = "deadbeef", Length = 3, Content = new byte[] { 1, 2, 3 } });
                db.SaveChanges();
            }

            storage.Add(new AppEntry
            {
                Name = "App", FolderPath = dir,
                Versions = { new AppVersion { VersionNumber = "1.0", IsInitial = true, Files = { live } } },
            });

            await store.CollectGarbageAsync();

            Assert.True(await store.HasAsync(live.Checksum));   // referenced by a live version → kept
            Assert.False(await store.HasAsync("deadbeef"));     // unreferenced → collected
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CollectGarbage_DropsBlobs_Of_RetractedVersion()
    {
        using var factory = new TestDbFactory();
        var storage = new EfStorageService(factory, new DpapiSecretProtector());
        var store = new EfFileBlobStore(factory, storage);
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var content = System.Text.Encoding.UTF8.GetBytes("only in a retracted version");
            var rec = Record("old.dll", content);
            MakeTempFile(dir, rec.Path, content);
            await store.StoreAsync(dir, new[] { rec });

            var app = new AppEntry
            {
                Name = "App", FolderPath = dir,
                Versions = { new AppVersion { VersionNumber = "1.0", Status = VersionStatus.Retracted, Files = { rec } } },
            };
            storage.Add(app);

            await store.CollectGarbageAsync();
            Assert.False(await store.HasAsync(rec.Checksum));   // retracted → not a live reference
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Packaging_FallsBackToBlob_WhenSourceMissing()
    {
        using var factory = new TestDbFactory();
        var store = new EfFileBlobStore(factory, new EfStorageService(factory, new DpapiSecretProtector()));
        var srcDir = Directory.CreateTempSubdirectory().FullName;
        var outFile = Path.Combine(Path.GetTempPath(), $"pkg_{Guid.NewGuid():N}.ftu");
        try
        {
            var content = System.Text.Encoding.UTF8.GetBytes("payload bytes that live only in the database");
            var rec = Record("bin/app.dll", content);
            MakeTempFile(srcDir, rec.Path, content);
            await store.StoreAsync(srcDir, new[] { rec });

            // Simulate a different machine: the source folder no longer has the file.
            Directory.Delete(srcDir, true);

            var packaging = new PackagingService(store);
            var entry = new AppEntry { Name = "App", FolderPath = srcDir };
            var version = new AppVersion { VersionNumber = "1.0", PackageType = PackageType.Full };
            var progress = new Progress<string>(_ => { });

            await packaging.BuildAsync(entry, version, new[] { rec }, PackageType.Full,
                outFile, manifestPath: null, removedFiles: null, progress);
            await packaging.VerifyAsync(outFile, progress);   // throws if the blob bytes weren't embedded

            Assert.True(File.Exists(outFile));
        }
        finally { if (File.Exists(outFile)) File.Delete(outFile); }
    }

    [Fact]
    public async Task NullStore_FindsNothing()
    {
        IFileBlobStore store = new NullFileBlobStore();
        Assert.False(await store.HasAsync("abc"));
        Assert.Null(await store.GetAsync("abc"));
        await store.StoreAsync("ignored", Array.Empty<FileRecord>());   // no-op, no throw
        await store.CollectGarbageAsync();
    }
}

public class SharedCertificateStoreTests
{
    // A throwaway self-signed code-signing cert exported as a password-protected .pfx (no PowerShell needed).
    private static (byte[] pfx, string thumbprint) MakePfx(string subject, string password)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={subject}", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        return (cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, password),
                cert.Thumbprint);
    }

    [Fact]
    public async Task Save_List_Get_RoundTrips_WithoutPassword()
    {
        using var factory = new TestDbFactory();
        var store = new EfSharedCertificateStore(factory);
        var (pfx, thumb) = MakePfx("ForgeTek Test", "pw123");

        var id = await store.SaveAsync("ForgeTek Test", "Friendly", thumb, pfx, "alice");

        var list = await store.ListAsync();
        var entry = Assert.Single(list);
        Assert.Equal("ForgeTek Test", entry.Subject);
        Assert.Equal(thumb, entry.Thumbprint);
        Assert.Equal("alice", entry.CreatedBy);

        Assert.Equal(pfx, await store.GetPfxAsync(id));   // bytes round-trip

        // The row has no password column at all — the secret never lands in the DB.
        using var db = factory.CreateDbContext();
        Assert.DoesNotContain(typeof(ForgeTekUpdatePackager.Data.CertificateRow).GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Delete_RemovesCertificate()
    {
        using var factory = new TestDbFactory();
        var store = new EfSharedCertificateStore(factory);
        var (pfx, thumb) = MakePfx("ToDelete", "pw");
        var id = await store.SaveAsync("ToDelete", "f", thumb, pfx, null);

        await store.DeleteAsync(id);
        Assert.Empty(await store.ListAsync());
        Assert.Null(await store.GetPfxAsync(id));
    }

    [Fact]
    public void ReadThumbprint_MatchesCertificate()
    {
        var (pfx, thumb) = MakePfx("ThumbCheck", "secret");
        Assert.Equal(thumb, new CertificateService().ReadThumbprint(pfx, "secret"));
    }

    [Fact]
    public async Task NullStore_IsNotShared_AndEmpty()
    {
        ISharedCertificateStore store = new NullSharedCertificateStore();
        Assert.False(store.IsShared);
        Assert.Empty(await store.ListAsync());
        Assert.Null(await store.GetPfxAsync("x"));
    }
}

public class LogServiceTests
{
    [Fact]
    public void ReadRange_FiltersByDate_AndPrefixesDate()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            File.WriteAllText(Path.Combine(logs, "2026-06-20.log"), "[09:00:00.000] [Scan] twentieth\n");
            File.WriteAllText(Path.Combine(logs, "2026-06-21.log"), "[10:00:00.000] [Scan] twenty-first\n");
            File.WriteAllText(Path.Combine(logs, "2026-06-22.log"), "[11:00:00.000] [Scan] twenty-second\n");

            var settings = NSubstitute.Substitute.For<ForgeTekUpdatePackager.Services.ISettingsService>();
            settings.RootFolder.Returns(root);
            var svc = new ForgeTekUpdatePackager.Services.LogService(settings);

            // Single day.
            var oneDay = svc.ReadRange(new DateOnly(2026, 6, 21), new DateOnly(2026, 6, 21));
            Assert.Equal(new[] { "2026-06-21 [10:00:00.000] [Scan] twenty-first" }, oneDay);

            // Range, chronological (oldest first), excludes the 22nd.
            var range = svc.ReadRange(new DateOnly(2026, 6, 20), new DateOnly(2026, 6, 21));
            Assert.Equal(2, range.Count);
            Assert.Contains("twentieth", range[0]);
            Assert.Contains("twenty-first", range[1]);
            Assert.DoesNotContain(range, l => l.Contains("twenty-second"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReadRange_SwapsReversedBounds_AndEmptyWhenNoFolder()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            File.WriteAllText(Path.Combine(logs, "2026-06-20.log"), "[09:00:00.000] [X] a\n");

            var settings = NSubstitute.Substitute.For<ForgeTekUpdatePackager.Services.ISettingsService>();
            settings.RootFolder.Returns(root);
            var svc = new ForgeTekUpdatePackager.Services.LogService(settings);

            // From > To is tolerated (swapped).
            Assert.Single(svc.ReadRange(new DateOnly(2026, 6, 21), new DateOnly(2026, 6, 19)));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class BackupServiceTests
{
    [Fact]
    public async Task Backup_Then_Restore_RoundTrips_AllData()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"bk_{Guid.NewGuid():N}.zip");
        var rootFolder = Directory.CreateTempSubdirectory().FullName;
        using var source = new TestDbFactory();
        using var target = new TestDbFactory();
        try
        {
            // Seed the source database.
            using (var db = source.CreateDbContext())
            {
                db.Users.Add(new ForgeTekUpdatePackager.Data.UserRow { UsernameKey = "alice", Payload = "{\"role\":\"Admin\"}" });
                db.GlobalSettingsRows.Add(new ForgeTekUpdatePackager.Data.GlobalSettingsRow { Id = 1, Payload = "{\"companyName\":\"Acme\"}", ProtectionEnabled = true });
                db.Apps.Add(new ForgeTekUpdatePackager.Data.AppRow { Id = "app1", Name = "MyApp", Payload = "{\"name\":\"MyApp\"}" });
                db.FileBlobs.Add(new ForgeTekUpdatePackager.Data.FileBlobRow { Sha256 = "abc123", Length = 4, Compressed = false, Content = new byte[] { 1, 2, 3, 4 } });
                db.Certificates.Add(new ForgeTekUpdatePackager.Data.CertificateRow { Id = "cert1", Subject = "CN=Test", FriendlyName = "f", Thumbprint = "THUMB", Pfx = new byte[] { 9, 8, 7 } });
                db.SaveChanges();
            }

            var progress = new Progress<string>(_ => { });
            await new BackupService(source).CreateBackupAsync(rootFolder, "nope/global.json", zipPath,
                includeApps: true, includeSetups: true, progress, CancellationToken.None);

            var restoredUsers = await new BackupService(target).RestoreAsync(zipPath, progress, CancellationToken.None);
            Assert.Equal(1, restoredUsers);

            using (var db = target.CreateDbContext())
            {
                Assert.Equal("alice", db.Users.Single().UsernameKey);
                Assert.Contains("Acme", db.GlobalSettingsRows.Single().Payload);
                Assert.True(db.GlobalSettingsRows.Single().ProtectionEnabled);
                Assert.Equal("MyApp", db.Apps.Single().Name);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, db.FileBlobs.Single().Content);   // binary blob round-trips
                Assert.Equal(new byte[] { 9, 8, 7 }, db.Certificates.Single().Pfx);       // pfx round-trips
            }
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            Directory.Delete(rootFolder, true);
        }
    }

    [Fact]
    public async Task Restore_Upserts_DoesNotDuplicate()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"bk_{Guid.NewGuid():N}.zip");
        var rootFolder = Directory.CreateTempSubdirectory().FullName;
        using var factory = new TestDbFactory();
        try
        {
            using (var db = factory.CreateDbContext())
            {
                db.Apps.Add(new ForgeTekUpdatePackager.Data.AppRow { Id = "app1", Name = "Original", Payload = "{}" });
                db.SaveChanges();
            }

            var progress = new Progress<string>(_ => { });
            var svc = new BackupService(factory);
            await svc.CreateBackupAsync(rootFolder, "x", zipPath, true, true, progress, CancellationToken.None);

            // Mutate, then restore the backup over it → upsert restores the original, no duplicate row.
            using (var db = factory.CreateDbContext())
            {
                var app = db.Apps.Single();
                app.Name = "Changed";
                db.SaveChanges();
            }

            await svc.RestoreAsync(zipPath, progress, CancellationToken.None);

            using (var db = factory.CreateDbContext())
            {
                var app = Assert.Single(db.Apps);
                Assert.Equal("Original", app.Name);
            }
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            Directory.Delete(rootFolder, true);
        }
    }
}

public class ApprovalServiceDbTests
{
    [Fact]
    public void Add_Votes_DerivesApprovedState()
    {
        using var factory = new TestDbFactory();
        IApprovalService svc = new ApprovalService(factory);
        var key = ReleaseApproval.ForApp("app1", "2.0");

        Assert.Equal(ApprovalState.Pending, svc.GetState(key));
        svc.Add(new ReleaseApproval { TargetKey = key, Decision = ApprovalDecision.Approve, ByRole = UserRole.Admin, ByUser = "a" });
        Assert.Equal(ApprovalState.Pending, svc.GetState(key));
        Assert.Equal(1, svc.ApprovalsSatisfied(key));
        svc.Add(new ReleaseApproval { TargetKey = key, Decision = ApprovalDecision.Approve, ByRole = UserRole.QaTester, ByUser = "q" });
        Assert.Equal(ApprovalState.Approved, svc.GetState(key));
        Assert.True(svc.IsApproved(key));
    }
}
