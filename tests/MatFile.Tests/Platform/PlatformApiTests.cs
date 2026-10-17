using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatFile.Web.Data;
using MatFile.Web.Platform;
using MatFile.Web.Services;
using MatPlatform;
using Microsoft.Extensions.DependencyInjection;

namespace MatFile.Tests.Platform;

/// <summary>Die Beschreibung von MatFile ist für den Validator von MatPlatform gültig, in beiden Sprachen und gegen das Sprite von MatUi.</summary>
public sealed class PlatformDefinitionTests(MatFileFactory site) : IClassFixture<MatFileFactory>
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_is_valid(string language)
        => Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MatFilePlatform.Describe, language), Sprite()));

    [Fact]
    public async Task The_served_definition_is_valid_and_names_the_app()
    {
        var json = await site.CreateClient().GetStringAsync("/api");
        Assert.Empty(MatPlatformValidator.Validate(json, Sprite()));

        using var doc = JsonDocument.Parse(json);
        var app = doc.RootElement.GetProperty("app");
        Assert.Equal(1, doc.RootElement.GetProperty("contract").GetInt32());
        Assert.Equal("matfile", app.GetProperty("id").GetString());
        Assert.Equal("#3472d8", app.GetProperty("accent").GetString());
        Assert.Equal(AppInfo.CurrentVersion, app.GetProperty("version").GetString());
        Assert.True(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("auth").TryGetProperty("identity", out _));
        Assert.Equal(["overview", "settings"], doc.RootElement.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(["admin", "user"], doc.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("key").GetString()));
        Assert.True(doc.RootElement.GetProperty("configure").GetArrayLength() <= 10);
    }

    [Fact]
    public async Task The_texts_follow_accept_language()
    {
        Assert.Contains("\"Speicherorte\"", await GetAsync("de"));
        Assert.Contains("\"Storage locations\"", await GetAsync("en"));

        async Task<string> GetAsync(string language)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
            request.Headers.AcceptLanguage.ParseAdd(language);
            return await (await site.CreateClient().SendAsync(request)).Content.ReadAsStringAsync();
        }
    }

    [Fact]
    public async Task The_definition_has_an_etag_and_answers_304()
    {
        var client = site.CreateClient();
        var first = await client.GetAsync("/api");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var again = new HttpRequestMessage(HttpMethod.Get, "/api");
        again.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    internal static IReadOnlySet<string> Sprite()
        => MatPlatformValidator.ReadSprite(Path.Combine(AppContext.BaseDirectory, "Fixtures", "matui-icons.svg"));
}

/// <summary>Die Endpunkte: Formen, Statuscodes, Schlüssel, Geheimnisse - und MatFiles eigene API unter /api bleibt, wie sie war.</summary>
public sealed class PlatformApiTests(MatFileFactory site) : IClassFixture<MatFileFactory>
{
    private const string RootPassword = "root-password-never-shown";

    [Fact]
    public async Task Health_is_anonymous_and_ok()
    {
        var response = await site.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("matfile", doc.RootElement.GetProperty("app").GetString());
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("ok", checks["database"]);
        Assert.Equal("ok", checks["data"]);
    }

    [Theory]
    [InlineData("GET", "/api/state")]
    [InlineData("GET", "/api/state/roots")]
    [InlineData("GET", "/api/configure")]
    [InlineData("PUT", "/api/configure")]
    [InlineData("POST", "/api/actions/checkStorage")]
    public async Task Without_the_key_it_is_401(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : JsonContent.Create(new { values = new { } }) };
        var response = await site.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var wrong = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : JsonContent.Create(new { values = new { } }) };
        wrong.Headers.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.CreateClient().SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task State_has_every_tile_with_real_values()
    {
        AddRoot("Daten", Path.Combine(site.DataDirectory, "share"));
        site.Db(db =>
        {
            var admin = db.User.Single(u => u.Username == "admin");
            db.JobTask.Add(new JobTask { Token = Guid.NewGuid(), UserId = admin.Id, JobType = JobType.Copy, Status = JobStatus.Failed, FinishedDate = DateTime.UtcNow.AddHours(-1) });
            db.JobTask.Add(new JobTask { Token = Guid.NewGuid(), UserId = admin.Id, JobType = JobType.Copy, Status = JobStatus.Failed, FinishedDate = DateTime.UtcNow.AddDays(-3) });
            db.ActivityLog.Add(new ActivityLog { UserId = admin.Id, ActionType = "Login" });
            return db.SaveChanges();
        });

        using var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/state"));
        var values = doc.RootElement.GetProperty("values");
        var definition = MatPlatformBuilder.Build(MatFilePlatform.Describe, "en");
        foreach (var tile in definition.State)
        {
            Assert.True(values.TryGetProperty(tile.Key, out _), tile.Key);
        }

        Assert.True(values.GetProperty("roots").GetProperty("value").GetInt32() >= 1);
        Assert.Equal(1, values.GetProperty("failedJobs24h").GetProperty("value").GetInt32());
        Assert.Equal("error", values.GetProperty("failedJobs24h").GetProperty("level").GetString());
        Assert.True(values.GetProperty("users").GetProperty("value").GetInt32() >= 1);
        Assert.Equal(0, values.GetProperty("rootsOffline").GetProperty("value").GetInt32());
        // Ein lokaler Speicherort kennt seine Belegung: Gesamt, frei und belegt sind da und passen zusammen.
        var total = values.GetProperty("storageTotal").GetProperty("value").GetInt64();
        var free = values.GetProperty("storageFree").GetProperty("value").GetInt64();
        Assert.True(total > 0 && free >= 0 && free <= total);
        Assert.Equal(total - free, values.GetProperty("storageUsed").GetProperty("value").GetInt64());
        Assert.True(DateTimeOffset.TryParse(values.GetProperty("lastActivity").GetProperty("value").GetString(), out _));
    }

    [Fact]
    public async Task One_state_value_by_key_and_404_for_an_unknown_key()
    {
        using var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/state/runningJobs"));
        Assert.Equal("runningJobs", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("value").ValueKind);

        var unknown = await site.Api().GetAsync("/api/state/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var errors = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.True(errors.RootElement.GetProperty("errors").TryGetProperty("nope", out _));
    }

    [Fact]
    public async Task Configure_reads_and_changes_the_settings_and_logs_who_did_it()
    {
        using (var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/configure")))
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal(14, values.GetProperty("sessionLifetimeDays").GetInt32());
            Assert.Equal(8, values.GetProperty("minPasswordLength").GetInt32());
            Assert.False(values.GetProperty("requireSecureCookie").GetBoolean());
        }

        var changed = await site.Api().PutAsJsonAsync("/api/configure", new { values = new { minPasswordLength = 12, maxUploadMegabytes = 500 } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using (var doc = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()))
        {
            Assert.Equal(12, doc.RootElement.GetProperty("values").GetProperty("minPasswordLength").GetInt32());
            Assert.Equal(500, doc.RootElement.GetProperty("values").GetProperty("maxUploadMegabytes").GetInt32());
        }

        // Dieselben Werte, die der Systemdialog und der Rest der App lesen.
        var settings = site.Services.GetRequiredService<MatFileConfigStore>().Settings;
        Assert.Equal(12, settings.MinPasswordLength);
        Assert.Equal(500, settings.MaxUploadMegabytes);
        Assert.Equal(14, settings.SessionLifetimeDays);

        var entry = site.Db(db => db.ActivityLog.OrderByDescending(a => a.Id).First(a => a.ActionType == "SettingsChanged"));
        Assert.Contains("api-key", entry.DetailsJson);
        Assert.Contains("MinPasswordLength", entry.DetailsJson);
    }

    [Fact]
    public async Task Configure_refuses_wrong_values_and_saves_nothing()
    {
        var before = site.Services.GetRequiredService<MatFileConfigStore>().Load().LoginLockoutMinutes;

        await AssertRefusedAsync(new { values = new { loginLockoutMinutes = 0 } }, "loginLockoutMinutes");
        await AssertRefusedAsync(new { values = new { loginLockoutMinutes = 30, minPasswordLength = 2 } }, "minPasswordLength");
        await AssertRefusedAsync(new { values = new { loginLockoutMinutes = 30, sessionLifetimeDays = 2.5 } }, "sessionLifetimeDays");
        await AssertRefusedAsync(new { values = new { requireSecureCookie = "yes" } }, "requireSecureCookie");
        await AssertRefusedAsync(new { values = new { adminPassword = "x" } }, "adminPassword");

        Assert.Equal(before, site.Services.GetRequiredService<MatFileConfigStore>().Load().LoginLockoutMinutes);

        var empty = await site.Api().PutAsync("/api/configure", new StringContent("nonsense", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        async Task AssertRefusedAsync(object body, string key)
        {
            var response = await site.Api().PutAsJsonAsync("/api/configure", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(key, out _), key);
        }
    }

    [Fact]
    public async Task No_answer_of_the_platform_contains_a_secret_or_a_path()
    {
        AddRoot("Geheim", Path.Combine(site.DataDirectory, "secret-share"), RootPassword);

        var answers = new[]
        {
            await site.CreateClient().GetStringAsync("/api"),
            await site.CreateClient().GetStringAsync("/api/health"),
            await site.Api().GetStringAsync("/api/state"),
            await site.Api().GetStringAsync("/api/configure"),
        };

        foreach (var answer in answers)
        {
            Assert.DoesNotContain(RootPassword, answer);
            Assert.DoesNotContain(site.DataDirectory, answer.Replace("\\\\", "\\"));
            Assert.DoesNotContain("PasswordHash", answer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MatFileFactory.ApiKey, answer);
        }

        // Ein Feld vom Typ secret würde als {"isSet": ...} gehen; MatFile hat keines unter den wenigen Einstellungen.
        Assert.DoesNotContain(MatPlatformBuilder.Build(MatFilePlatform.Describe, "en").Configure, f => f.Type == "secret");
    }

    [Fact]
    public async Task Check_storage_reports_the_reachable_locations()
    {
        AddRoot("Erreichbar", Path.Combine(site.DataDirectory, "reachable"));

        var response = await site.Api().PostAsync("/api/actions/checkStorage", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MatActionResult>(MatApiDefinition.Json);
        Assert.True(result!.Ok);
        Assert.Contains("storage locations reachable", (await ReadInEnglishAsync("/api/actions/checkStorage")));
    }

    [Fact]
    public async Task Cancel_jobs_cancels_waiting_jobs_and_is_409_when_nothing_runs()
    {
        var token = site.Db(db =>
        {
            var admin = db.User.Single(u => u.Username == "admin");
            var job = new JobTask { Token = Guid.NewGuid(), UserId = admin.Id, JobType = JobType.Delete, Status = JobStatus.Pending };
            db.JobTask.Add(job);
            db.SaveChanges();
            return job.Token;
        });

        var response = await site.Api().PostAsync("/api/actions/cancelJobs", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JobStatus.Cancelled, site.Db(db => db.JobTask.Single(j => j.Token == token).Status));
        Assert.True(site.Db(db => db.ActivityLog.Any(a => a.ActionType == "JobsCancelled")));

        var nothing = await site.Api().PostAsync("/api/actions/cancelJobs", null);
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
        var result = await nothing.Content.ReadFromJsonAsync<MatActionResult>(MatApiDefinition.Json);
        Assert.False(result!.Ok);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("open")]
    [InlineData("activity")]
    public async Task An_unknown_action_or_a_link_is_404(string id)
        => Assert.Equal(HttpStatusCode.NotFound, (await site.Api().PostAsync("/api/actions/" + id, null)).StatusCode);

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/fs/roots")]
    [InlineData("/api/jobs")]
    [InlineData("/api/users")]
    [InlineData("/api/system")]
    public async Task The_own_api_of_matfile_is_unchanged_and_the_key_opens_nothing_there(string path)
    {
        // Ohne Anmeldung 401 wie bisher - auch mit dem Schlüssel: der gilt nur für den Plattformvertrag.
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api().GetAsync(path)).StatusCode);
    }

    private async Task<string> ReadInEnglishAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Api-Key", MatFileFactory.ApiKey);
        request.Headers.AcceptLanguage.ParseAdd("en");
        return await (await site.CreateClient().SendAsync(request)).Content.ReadAsStringAsync();
    }

    private void AddRoot(string name, string path, string? password = null)
    {
        Directory.CreateDirectory(path);
        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatFileDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        if (db.StorageRoot.Any(r => r.Name == name))
        {
            return;
        }

        db.StorageRoot.Add(new StorageRoot
        {
            Name = name,
            StorageType = StorageType.Local,
            BasePath = path,
            IsEnabled = true,
            SecretEncrypted = password is null ? null : protector.Protect(password),
        });
        db.SaveChanges();
    }
}

/// <summary>Ohne <c>MAT_API_KEY</c> ist der Schlüssel aus: der geschützte Teil antwortet allen mit 401, /api und /api/health bleiben offen.</summary>
public sealed class PlatformWithoutKeyTests(NoKeyFactory site) : IClassFixture<NoKeyFactory>
{
    [Fact]
    public async Task Everything_with_values_is_401_and_the_definition_says_the_key_is_off()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api().GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api().GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.CreateClient().GetAsync("/api/health")).StatusCode);

        using var doc = JsonDocument.Parse(await site.CreateClient().GetStringAsync("/api"));
        Assert.False(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
    }
}

/// <summary>Die anonymen Endpunkte sind je Absender begrenzt (UseRateLimiter steht dafür hinter UseRouting).</summary>
public sealed class PlatformRateLimitTests
{
    [Fact]
    public async Task Health_is_rate_limited_per_caller()
    {
        using var site = new MatFileFactory();
        var client = site.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 125; i++)
        {
            statuses.Add((await client.GetAsync("/api/health")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }
}
