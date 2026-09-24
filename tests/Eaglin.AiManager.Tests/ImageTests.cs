using System.Net;
using System.Text.Json;
using Eaglin.AiManager.Providers;

namespace Eaglin.AiManager.Tests;

/// <summary>
/// Pictures sent with a prompt (1.3.0): each provider puts them where its own API expects them,
/// and a prompt with no pictures is sent exactly as it was before.
/// </summary>
public class ImageTests
{
    private static readonly AiImage Png = new([0x89, 0x50, 0x4E, 0x47], "image/png");

    [Fact]
    public async Task Claude_sends_the_picture_as_an_image_block_before_the_text()
    {
        var h = new FakeHandler(HttpStatusCode.OK,
            "{ \"content\": [ { \"type\": \"text\", \"text\": \"<svg/>\" } ], \"usage\": { \"input_tokens\": 1, \"output_tokens\": 1 } }");

        await new ClaudeProvider("k", "m", h.Client()).GenerateAsync("s", "draw it", 10, [Png], CancellationToken.None);

        JsonElement content = JsonDocument.Parse(h.LastBody!).RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Png.Base64, content[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("draw it", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Gemini_sends_the_picture_as_inline_data()
    {
        var h = new FakeHandler(HttpStatusCode.OK,
            "{ \"candidates\": [ { \"content\": { \"parts\": [ { \"text\": \"ok\" } ] } } ], \"usageMetadata\": {} }");

        await new GeminiProvider("g", "gemini-2.5-flash", h.Client()).GenerateAsync("s", "draw it", 10, [Png], CancellationToken.None);

        JsonElement parts = JsonDocument.Parse(h.LastBody!).RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("image/png", parts[0].GetProperty("inline_data").GetProperty("mime_type").GetString());
        Assert.Equal("draw it", parts[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(AiProviderType.OpenAi)]
    [InlineData(AiProviderType.Mistral)]
    [InlineData(AiProviderType.XAi)]
    public async Task Chat_completions_providers_send_the_picture_as_a_data_url(AiProviderType type)
    {
        var h = new FakeHandler(HttpStatusCode.OK,
            "{ \"choices\": [ { \"message\": { \"content\": \"ok\" } } ], \"usage\": { \"prompt_tokens\": 1, \"completion_tokens\": 1 } }");

        await AiProviderRouter.Build(type, "key", "m", h.Client()).GenerateAsync("s", "draw it", 10, [Png], CancellationToken.None);

        JsonElement content = JsonDocument.Parse(h.LastBody!).RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("draw it", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64," + Png.Base64, content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(AiProviderType.Claude)]
    [InlineData(AiProviderType.OpenAi)]
    public async Task Without_pictures_the_prompt_is_a_plain_string_as_before(AiProviderType type)
    {
        var h = new FakeHandler(HttpStatusCode.OK, type == AiProviderType.Claude
            ? "{ \"content\": [ { \"type\": \"text\", \"text\": \"ok\" } ], \"usage\": {} }"
            : "{ \"choices\": [ { \"message\": { \"content\": \"ok\" } } ], \"usage\": {} }");

        await AiProviderRouter.Build(type, "key", "m", h.Client()).GenerateAsync("s", "just text", 10, CancellationToken.None);

        JsonElement messages = JsonDocument.Parse(h.LastBody!).RootElement.GetProperty("messages");
        JsonElement user = messages[messages.GetArrayLength() - 1];
        Assert.Equal(JsonValueKind.String, user.GetProperty("content").ValueKind);
    }

    [Theory]
    [InlineData("card.png", "image/png")]
    [InlineData("card.JPG", "image/jpeg")]
    [InlineData("card.webp", "image/webp")]
    [InlineData("card.pdf", null)]
    public void Only_the_four_picture_types_every_vision_model_takes_are_recognised(string name, string? expected) =>
        Assert.Equal(expected, AiImage.MediaTypeFor(name));
}
