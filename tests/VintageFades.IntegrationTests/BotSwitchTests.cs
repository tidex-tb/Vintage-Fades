using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VintageFades.Domain;
using VintageFades.IntegrationTests.Infrastructure;

namespace VintageFades.IntegrationTests;

/// <summary>The owner's master switch: while the bot is off, no platform gets a reply, and every message is still saved.</summary>
[Collection(PostgresCollection.Name)]
public class BotSwitchTests(TestApp app) : IntegrationTest(app)
{
    private async Task<JsonElement> SetBotAsync(bool paused)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/admin/bot") { Content = JsonContent.Create(new { paused }) };
        request.Headers.Add("X-Admin-Key", TestApp.AdminKey);
        var response = await App.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task The_switch_needs_the_admin_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await App.Client.GetAsync("/api/admin/bot")).StatusCode);
        var put = await App.Client.PutAsJsonAsync("/api/admin/bot", new { paused = true });
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Fact]
    public async Task A_paused_bot_stays_silent_on_every_platform_and_resumes_when_switched_on()
    {
        Assert.True((await SetBotAsync(true)).GetProperty("paused").GetBoolean());
        try
        {
            Assert.Empty(await App.SendAsync(Platform.WhatsApp, "264811110001", "hi"));
            Assert.Empty(await App.SendAsync(Platform.Instagram, "ig-paused-1", "how much is a haircut"));
            Assert.Empty(await App.SendAsync(Platform.Messenger, "fb-paused-1", "hello"));
        }
        finally
        {
            Assert.False((await SetBotAsync(false)).GetProperty("paused").GetBoolean());
        }

        Assert.NotEmpty(await App.SendAsync(Platform.WhatsApp, "264811110001", "hi"));
        Assert.NotEmpty(await App.SendAsync(Platform.Instagram, "ig-paused-1", "hi"));
    }

    [Fact]
    public async Task The_owner_can_switch_the_bot_off_and_on_by_text_message()
    {
        try
        {
            var off = string.Join("\n", await App.SendAsync(Platform.WhatsApp, TestApp.OwnerNumber, "BOT OFF"));
            Assert.Contains("OFF", off);
            Assert.Empty(await App.SendAsync(Platform.WhatsApp, "264811110002", "hi"));
            Assert.Contains("OFF", string.Join("\n", await App.SendAsync(Platform.WhatsApp, TestApp.OwnerNumber, "bot")));
        }
        finally
        {
            Assert.Contains("ON again", string.Join("\n", await App.SendAsync(Platform.WhatsApp, TestApp.OwnerNumber, "BOT ON")));
        }
        Assert.NotEmpty(await App.SendAsync(Platform.WhatsApp, "264811110002", "hi"));
    }
}
