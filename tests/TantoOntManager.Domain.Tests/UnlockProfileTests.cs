using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TantoOntManager.Domain.Backup;
using TantoOntManager.Domain.Devices;
using TantoOntManager.Domain.Profiles;
using TantoOntManager.Domain.Unlock;

namespace TantoOntManager.Domain.Tests;

public sealed class UnlockProfileTests
{
    [Theory]
    [InlineData("F6201B", "ZXHN F6201B")]
    [InlineData("F670L", "ZXHN F670L")]
    [InlineData("F6600P", "F6600P")]
    public void Matches_catalog_model_ids_to_profile_keys(string profileModel, string observed)
    {
        ZteProfile.ModelKeysMatch(profileModel, observed).Should().BeTrue();
        new ZteProfile { Model = profileModel }.MatchesModel(observed).Should().BeTrue();
    }

    [Fact]
    public void Does_not_match_unrelated_models()
    {
        ZteProfile.ModelKeysMatch("F6201B", "ZXHN F670L").Should().BeFalse();
        ZteProfile.NormalizeModelKey(DeviceModelIds.ZteF6201B).Should().Be("F6201B");
    }

    [Fact]
    public void Repository_selects_homologated_f6201b_profile_from_catalog_id()
    {
        var repository = new ZteProfileRepository(NullLogger<ZteProfileRepository>.Instance);

        var profile = repository.FindExact(DeviceModelIds.ZteF6201B, "V9.3.10P8N1")
                      ?? repository.FindByModel(DeviceModelIds.ZteF6201B);

        profile.Should().NotBeNull();
        profile!.ProfileId.Should().Be("f6201b-v9310p8n1");
        profile.UnlockStrategy.Should().Be(UnlockStrategy.ConfigBinOnly);
        profile.IsHomologated.Should().BeTrue();
    }
}

public sealed class BackupServiceTests
{
    [Fact]
    public async Task CreateBackup_writes_bin_and_metadata()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tanto-backup-tests", Guid.NewGuid().ToString("N"));
        var service = new LocalBackupService(NullLogger<LocalBackupService>.Instance, folder);
        var payload = Enumerable.Repeat((byte)7, 2048).ToArray();

        var ticket = await service.CreateBackupAsync(
            payload,
            new OntIdentitySnapshot
            {
                Model = "F6201B",
                FirmwareVersion = "V9.3.10P8N1",
                SerialNumber = "ZTEG12345678"
            });

        ticket.IsRestorable.Should().BeTrue();
        File.Exists(ticket.FilePath).Should().BeTrue();
        (await File.ReadAllBytesAsync(ticket.FilePath)).Should().Equal(payload);

        var listed = await service.ListBackupsAsync("ZTEG12345678");
        listed.Should().ContainSingle(item => item.TicketId == ticket.TicketId);
    }
}
