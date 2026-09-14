using Launcher.Models.Profiles;
using Launcher.Models.Scanning;

namespace Launcher.Tests;

public sealed class JsonModelProfileStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsProfileInRuntimeScriptsDirectory()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var candidate = CreateCandidate(root, "Coder Q4_K_M.gguf");
            var profile = ModelProfileFactory.SaveCurrentParametersAsDefault(
                ModelProfileFactory.CreateDefault(candidate, root) with
                {
                    ContextSize = 32768,
                    CacheTypeK = "q8_0",
                    CacheTypeV = "q8_0",
                    ChatTemplateRelativePath = Path.Combine("scripts", "templates", "coder.jinja"),
                });
            var store = new JsonModelProfileStore();

            await store.SaveAsync(root, profile);
            var result = await store.LoadAsync(root);

            var loaded = Assert.Single(result.Profiles);
            Assert.Equal(profile.Id, loaded.Id);
            Assert.Equal(profile.DisplayName, loaded.DisplayName);
            Assert.Equal(profile.ModelRelativePath, loaded.ModelRelativePath);
            Assert.Equal(profile.Alias, loaded.Alias);
            Assert.Equal(profile.ContextSize, loaded.ContextSize);
            Assert.Equal(profile.CompactionSafetyReserve, loaded.CompactionSafetyReserve);
            Assert.Equal(profile.GpuLayers, loaded.GpuLayers);
            Assert.Equal(profile.FlashAttention, loaded.FlashAttention);
            Assert.Equal(profile.CacheTypeK, loaded.CacheTypeK);
            Assert.Equal(profile.CacheTypeV, loaded.CacheTypeV);
            Assert.Equal(profile.Parallel, loaded.Parallel);
            Assert.Equal(profile.Jinja, loaded.Jinja);
            Assert.Equal(profile.IdleSleepSeconds, loaded.IdleSleepSeconds);
            Assert.Equal(profile.ChatTemplateRelativePath, loaded.ChatTemplateRelativePath);
            Assert.Empty(loaded.ExtraArguments);
            Assert.NotNull(loaded.DefaultParameters);
            Assert.Equal(32768, loaded.DefaultParameters.ContextSize);
            Assert.Equal(8192, loaded.DefaultParameters.CompactionSafetyReserve);
            Assert.Equal("q8_0", loaded.DefaultParameters.CacheTypeK);
            Assert.Equal(profile.ChatTemplateRelativePath, loaded.DefaultParameters.ChatTemplateRelativePath);
            Assert.Empty(result.Diagnostics);
            Assert.True(File.Exists(Path.Combine(root, "scripts", "profiles", profile.Id + ".json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WhenOneProfileIsInvalid_ReturnsValidProfilesAndDiagnostic()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var store = new JsonModelProfileStore();
            var profile = ModelProfileFactory.CreateDefault(CreateCandidate(root, "Coder.gguf"), root);
            await store.SaveAsync(root, profile);
            await File.WriteAllTextAsync(
                Path.Combine(root, "scripts", "profiles", "broken.json"),
                "{ not-json }");

            var result = await store.LoadAsync(root);

            Assert.Single(result.Profiles);
            Assert.Single(result.Diagnostics);
            Assert.Contains("broken.json", result.Diagnostics[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WhenProfileIsInvalid_ReportsBackupWithoutApplyingIt()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var store = new JsonModelProfileStore();
            var profile = ModelProfileFactory.CreateDefault(CreateCandidate(root, "Coder.gguf"), root);
            await store.SaveAsync(root, profile);
            await store.SaveAsync(root, profile);
            var path = Path.Combine(root, "scripts", "profiles", profile.Id + ".json");
            await File.WriteAllTextAsync(path, "{ invalid-json }");

            var result = await store.LoadAsync(root);

            Assert.Empty(result.Profiles);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Contains(".bak", diagnostic, StringComparison.Ordinal);
            Assert.Contains("未自动恢复", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WhenAliasesConflict_ExcludesDuplicateAlias()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var store = new JsonModelProfileStore();
            var first = ModelProfileFactory.CreateDefault(CreateCandidate(root, "Alpha.gguf"), root);
            var second = ModelProfileFactory.CreateDefault(CreateCandidate(root, "Beta.gguf"), root) with
            {
                Alias = first.Alias,
            };
            await store.SaveAsync(root, first);
            await store.SaveAsync(root, second);

            var result = await store.LoadAsync(root);

            Assert.Single(result.Profiles);
            Assert.Single(result.Diagnostics);
            Assert.Contains("Alias", result.Diagnostics[0], StringComparison.Ordinal);
            Assert.Contains("重复", result.Diagnostics[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_LegacyProfileWithoutIdleSleep_UsesNativeFiveMinuteDefault()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var profileDirectory = Path.Combine(root, "scripts", "profiles");
            Directory.CreateDirectory(profileDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(profileDirectory, "legacy.json"),
                """
                {
                  "SchemaVersion": 1,
                  "Id": "legacy",
                  "DisplayName": "Legacy",
                  "ModelRelativePath": "models/Legacy.gguf",
                  "Alias": "legacy",
                  "ContextSize": 16384
                }
                """);

            var result = await new JsonModelProfileStore().LoadAsync(root);

            var loaded = Assert.Single(result.Profiles);
            Assert.Equal(300, loaded.IdleSleepSeconds);
            Assert.Equal(ModelProfile.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(ModelSourceKind.LocalFile, loaded.SourceKind);
            Assert.Equal(4096, loaded.CompactionSafetyReserve);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_V2Profile_MigratesPerModelCompactionReserve()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var profileDirectory = Path.Combine(root, "scripts", "profiles");
            Directory.CreateDirectory(profileDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(profileDirectory, "legacy-v2.json"),
                """
                {
                  "SchemaVersion": 2,
                  "Id": "legacy-v2",
                  "DisplayName": "Legacy V2",
                  "ModelRelativePath": "models/Legacy.gguf",
                  "Alias": "legacy-v2",
                  "ContextSize": 32768,
                  "DefaultParameters": {
                    "ContextSize": 16384
                  }
                }
                """);

            var result = await new JsonModelProfileStore().LoadAsync(root);

            var loaded = Assert.Single(result.Profiles);
            Assert.Equal(ModelProfile.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(8192, loaded.CompactionSafetyReserve);
            Assert.NotNull(loaded.DefaultParameters);
            Assert.Equal(4096, loaded.DefaultParameters.CompactionSafetyReserve);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateDefault_PreservesVerifiedLlamaDownloadProvenance()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var local = CreateCandidate(root, "download.gguf");
            var candidate = local with
            {
                RemoteModelId = "org/model:Q4_K_M",
                RemoteRepositoryId = "org/model",
                RemoteQuantization = "Q4_K_M",
            };

            var profile = ModelProfileFactory.CreateDefault(candidate, root);

            Assert.Equal(ModelSourceKind.LlamaCache, profile.SourceKind);
            Assert.Equal("org/model:Q4_K_M", profile.RemoteModelId);
            Assert.Equal(3, profile.KnownSizeBytes);
            Assert.Equal(1, profile.KnownShardCount);
            Assert.Empty(ModelProfileValidator.Validate(profile, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteLauncherMetadata_RefusesUserOwnedLocalProfile()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var profile = ModelProfileFactory.CreateDefault(CreateCandidate(root, "local.gguf"), root);
            Assert.Throws<InvalidDataException>(() =>
                new JsonModelProfileStore().DeleteLauncherMetadata(root, profile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateDefault_WhenIdentifierExists_UsesUniqueIdentifier()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var candidate = CreateCandidate(root, "Coder.gguf");
            var first = ModelProfileFactory.CreateDefault(candidate, root);
            var secondCandidate = CreateCandidate(root, Path.Combine("other", "Coder.gguf"));

            var second = ModelProfileFactory.CreateDefault(secondCandidate, root, new[] { first });

            Assert.Equal("coder-2", second.Id);
            Assert.Equal("coder-2", second.Alias);
            Assert.Equal(Path.Combine("models", "other", "Coder.gguf"), second.ModelRelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateDefault_UsesLlamaNativeValuesWithoutClaimingHardwareRecommendation()
    {
        var root = CreateRuntimeRoot();
        try
        {
            var profile = ModelProfileFactory.CreateDefault(CreateCandidate(root, "Coder.gguf"), root);

            Assert.Equal(0, profile.ContextSize);
            Assert.Equal(8192, profile.CompactionSafetyReserve);
            Assert.Equal("auto", profile.GpuLayers);
            Assert.Null(profile.Device);
            Assert.Equal("auto", profile.FlashAttention);
            Assert.Equal("f16", profile.CacheTypeK);
            Assert.Equal("f16", profile.CacheTypeV);
            Assert.Equal(-1, profile.Parallel);
            Assert.True(profile.Jinja);
            Assert.Null(profile.BatchSize);
            Assert.Null(profile.MicroBatchSize);
            Assert.Equal(300, profile.IdleSleepSeconds);
            Assert.Empty(profile.ExtraArguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ModelSpecificDefaults_AffectOnlyTheirOwningProfile()
    {
        var first = new ModelProfile
        {
            Id = "first",
            DisplayName = "First",
            ModelRelativePath = Path.Combine("models", "First.gguf"),
            Alias = "first",
            ContextSize = 32768,
            CacheTypeK = "q8_0",
            CacheTypeV = "q8_0",
            Parallel = 2,
        };
        var second = first with
        {
            Id = "second",
            DisplayName = "Second",
            ModelRelativePath = Path.Combine("models", "Second.gguf"),
            Alias = "second",
            ContextSize = 65536,
        };
        var firstWithDefault = ModelProfileFactory.SaveCurrentParametersAsDefault(first);

        var restoredFirst = ModelProfileFactory.RestoreModelDefaults(firstWithDefault with
        {
            ContextSize = 16384,
            CompactionSafetyReserve = 4096,
            CacheTypeK = "q4_0",
            CacheTypeV = "q4_0",
            Parallel = 1,
        });
        var restoredSecond = ModelProfileFactory.RestoreModelDefaults(second);

        Assert.Equal(32768, restoredFirst.ContextSize);
        Assert.Equal(1024, restoredFirst.CompactionSafetyReserve);
        Assert.Equal("q8_0", restoredFirst.CacheTypeK);
        Assert.Equal("q8_0", restoredFirst.CacheTypeV);
        Assert.Equal(2, restoredFirst.Parallel);
        Assert.Equal("first", restoredFirst.Id);
        Assert.NotNull(restoredFirst.DefaultParameters);
        Assert.Equal(65536, restoredSecond.ContextSize);
        Assert.Equal("q8_0", restoredSecond.CacheTypeK);
        Assert.Equal("second", restoredSecond.Id);
        Assert.Null(restoredSecond.DefaultParameters);
    }

    private static string CreateRuntimeRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "ChatGPTLocalLauncher.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "models"));
        return path;
    }

    private static GgufModelCandidate CreateCandidate(string root, string relativeName)
    {
        var path = Path.Combine(root, "models", relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return new GgufModelCandidate(
            path,
            relativeName,
            Path.GetFileNameWithoutExtension(relativeName),
            TotalSizeBytes: 3,
            ShardCount: 1);
    }
}
