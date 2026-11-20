using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MicrosoftDecisionConnector.Tests;

public class ImageTests
{
    private const string CatPng = "siamese-cat.png";
    private const string AstronautJpg = "astronaut-eva.jpg";

    private static readonly JArray NoulQuestion = new JArray(new JObject { ["id"] = "q", ["type"] = "noul", ["instructions"] = "Does the image show a person?" });

    private static HttpResponseMessage Ok(HttpRequestMessage _) => Harness.Json(HttpStatusCode.OK, Harness.Fixture("systemone-response.json"));

    [Fact]
    public async Task Image_rows_become_native_assets_and_unreferenced_images_are_appended_to_state()
    {
        var png = Harness.Asset(CatPng);
        var jpg = Harness.Asset(AstronautJpg);
        var body = new JObject
        {
            ["state"] = "Compare image:cat.png with the second picture.",
            ["questions"] = NoulQuestion,
            ["images"] = new JArray(
                new JObject { ["name"] = "cat.png", ["content"] = Convert.ToBase64String(png) },
                new JObject { ["name"] = "astronaut.jpg", ["content"] = Convert.ToBase64String(jpg) })
        };

        var sent = await SendAsync(body);

        Assert.Equal("Compare image:cat.png with the second picture.\n\nimage:astronaut.jpg", (string)sent["state"]);
        Assert.Equal(new[] { "model", "state", "questions", "assets" }, sent.Properties().Select(p => p.Name));
        Assert.True(JToken.DeepEquals(
            new JObject
            {
                ["cat.png"] = new JObject { ["type"] = "image", ["format"] = "png", ["data_base64"] = Convert.ToBase64String(png) },
                ["astronaut.jpg"] = new JObject { ["type"] = "image", ["format"] = "jpeg", ["data_base64"] = Convert.ToBase64String(jpg) }
            },
            sent["assets"]));
    }

    [Theory]
    [InlineData(CatPng, "png", 960, 763)]
    [InlineData(AstronautJpg, "jpeg", 960, 960)]
    public void Real_images_are_inspected_from_their_headers(string file, string format, int width, int height)
    {
        int actualWidth;
        int actualHeight;
        var actual = Script.InspectImage(Harness.Asset(file), "images[0].content", out actualWidth, out actualHeight);

        Assert.Equal(format, actual);
        Assert.Equal(width, actualWidth);
        Assert.Equal(height, actualHeight);
    }

    public static TheoryData<JToken> ContentForms()
    {
        var base64 = Convert.ToBase64String(Images.Png(400, 300));
        return new TheoryData<JToken>
        {
            base64,
            "  " + base64 + "\n",
            "data:image/png;base64," + base64,
            string.Join("\r\n", Enumerable.Range(0, (base64.Length + 75) / 76).Select(i => base64.Substring(i * 76, Math.Min(76, base64.Length - i * 76)))),
            new JObject { ["$content-type"] = "image/png", ["$content"] = base64 },
            // Power Automate base64-encodes text passed to a byte parameter a second time.
            Convert.ToBase64String(Encoding.ASCII.GetBytes(base64)),
            Convert.ToBase64String(Encoding.ASCII.GetBytes("data:image/png;base64," + base64))
        };
    }

    [Theory]
    [MemberData(nameof(ContentForms))]
    public async Task Content_is_accepted_as_base64_data_uri_or_file_object(JToken content)
    {
        var sent = await SendAsync(WithImages("image:a", new JArray(new JObject { ["name"] = "a", ["content"] = content })));

        Assert.Equal(Convert.ToBase64String(Images.Png(400, 300)), (string)sent["assets"]["a"]["data_base64"]);
        Assert.Equal("png", (string)sent["assets"]["a"]["format"]);
    }

    public static TheoryData<string, JToken> ReferencedImages() => new TheoryData<string, JToken>
    {
        { "Picture: image:a", NoulQuestion },
        { "Picture: image:a.", NoulQuestion },
        { "Picture: (image:a)", NoulQuestion },
        { "s", new JArray(new JObject { ["id"] = "q", ["type"] = "noul", ["instructions"] = "Is image:a damaged?" }) },
        { "s", new JArray(new JObject { ["id"] = "q", ["type"] = "noul", ["noulTrue"] = "Looks like image:a" }) },
        { "s", new JArray(new JObject { ["id"] = "q", ["type"] = "choice", ["options"] = new JArray(new JObject { ["option"] = "first", ["description"] = "image:a" }, new JObject { ["option"] = "second" }) }) },
        { "s", new JArray(new JObject { ["id"] = "q", ["type"] = "score", ["levels"] = new JArray("unlike image:a", "like image:a") }) },
        { "s", new JObject { ["q"] = new JObject { ["type"] = "noul", ["instructions"] = new JObject { ["text"] = "Compare with image:a" } } } }
    };

    [Theory]
    [MemberData(nameof(ReferencedImages))]
    public async Task Referenced_images_leave_state_unchanged(string state, JToken questions)
    {
        var body = WithImages(state, Rows("a"));
        body["questions"] = questions;

        var sent = await SendAsync(body);

        Assert.Equal(state, (string)sent["state"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Empty_state_becomes_the_image_references(string state)
    {
        var body = WithImages(state, Rows("a", "b"));

        var sent = await SendAsync(body);

        Assert.Equal("image:a image:b", (string)sent["state"]);
    }

    [Fact]
    public async Task Json_state_that_references_every_image_is_sent_unchanged()
    {
        var sent = await SendAsync(WithImages("{\"photo\":\"image:a\",\"count\":3}", Rows("a")));

        Assert.True(JToken.DeepEquals(new JObject { ["photo"] = "image:a", ["count"] = 3 }, sent["state"]));
    }

    [Theory]
    [InlineData("{\"message\":\"Checkout is down\"}")]
    [InlineData("[\"image:b\"]")]
    public async Task Json_state_with_an_unreferenced_image_is_rejected(string state)
    {
        await AssertRejectedAsync(WithImages(state, Rows("a")), "unreferenced_image", "images[0]");
    }

    [Theory]
    [InlineData("image:photo.png", "photo.png", true)]
    [InlineData("See image:photo.png.", "photo.png", true)]
    [InlineData("See image:photo.png.\nNext", "photo.png", true)]
    [InlineData("image:photo.png, image:other", "photo.png", true)]
    [InlineData("image:photo.pngx", "photo.png", false)]
    [InlineData("image:photo.png-2", "photo.png", false)]
    [InlineData("image:photo.png.bak", "photo.png", false)]
    [InlineData("image:a.b", "a", false)]
    [InlineData("image:ab image:a", "a", true)]
    [InlineData("image: a", "a", false)]
    [InlineData("Image:a", "a", false)]
    public void Image_references_need_a_name_boundary(string text, string name, bool expected)
    {
        Assert.Equal(expected, Script.ContainsImageReference(text, name));
    }

    [Theory]
    [InlineData("images")]
    [InlineData("assets")]
    public async Task Native_assets_object_is_validated_and_normalized(string property)
    {
        var jpg = Images.Jpeg(640, 480);
        var body = new JObject
        {
            ["state"] = "image:photo",
            ["questions"] = NoulQuestion,
            [property] = new JObject { ["photo"] = new JObject { ["type"] = "image", ["format"] = "jpg", ["data_base64"] = Convert.ToBase64String(jpg) } }
        };

        var sent = await SendAsync(body);

        Assert.True(JToken.DeepEquals(
            new JObject { ["photo"] = new JObject { ["type"] = "image", ["format"] = "jpeg", ["data_base64"] = Convert.ToBase64String(jpg) } },
            sent["assets"]));
    }

    [Fact]
    public async Task Images_can_be_passed_as_json_text()
    {
        var rows = Rows("a").ToString();

        var sent = await SendAsync(WithImages("image:a", rows));

        Assert.Equal("png", (string)sent["assets"]["a"]["format"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Missing_or_empty_images_send_no_assets(string images)
    {
        var body = new JObject { ["state"] = "text", ["questions"] = NoulQuestion };
        if (images != null)
        {
            body["images"] = images.Length == 0 ? new JValue(images) : JToken.Parse(images);
        }

        var sent = await SendAsync(body);

        Assert.Equal("text", (string)sent["state"]);
        Assert.Null(sent["assets"]);
    }

    [Theory]
    [InlineData(280, 280)]
    [InlineData(1568, 1568)]
    [InlineData(280, 1568)]
    public async Task Boundary_dimensions_are_accepted(int width, int height)
    {
        var rows = new JArray(
            new JObject { ["name"] = "p", ["content"] = Convert.ToBase64String(Images.Png(width, height)) },
            new JObject { ["name"] = "j", ["content"] = Convert.ToBase64String(Images.Jpeg(width, height)) });

        var sent = await SendAsync(WithImages("image:p image:j", rows));

        Assert.Equal(2, ((JObject)sent["assets"]).Count);
    }

    [Fact]
    public async Task Progressive_jpeg_is_accepted()
    {
        var rows = new JArray(new JObject { ["name"] = "j", ["content"] = Convert.ToBase64String(Images.Jpeg(800, 600, sofMarker: 0xC2)) });

        var sent = await SendAsync(WithImages("image:j", rows));

        Assert.Equal("jpeg", (string)sent["assets"]["j"]["format"]);
    }

    public static TheoryData<JObject, string, string> InvalidImages()
    {
        static JObject Row(string name, byte[] content, string format = null)
        {
            var row = new JObject { ["name"] = name, ["content"] = Convert.ToBase64String(content) };
            if (format != null)
            {
                row["format"] = format;
            }

            return row;
        }

        static JObject Body(params JToken[] rows) => WithImages("image:a", new JArray(rows));

        var png = Images.Png(400, 400);
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x90, 0x01, 0x90, 0x01, 0, 0, 0 };
        var webp = Encoding.ASCII.GetBytes("RIFF\x10\0\0\0WEBPVP8 \0\0\0\0");
        var both = Body(Row("a", png));
        both["assets"] = new JObject { ["b"] = new JObject { ["data_base64"] = Convert.ToBase64String(png) } };

        return new TheoryData<JObject, string, string>
        {
            { Body(new JObject { ["content"] = Convert.ToBase64String(png) }), "missing_image_name", "images[0].name" },
            { Body(Row("my photo", png)), "invalid_image_name", "images[0].name" },
            { Body(Row("image:a", png)), "invalid_image_name", "images[0].name" },
            { Body(Row(new string('a', 65), png)), "invalid_image_name", "images[0].name" },
            { Body(Row("a", png), Row("a", png)), "duplicate_image_name", "images[1].name" },
            { Body(new JObject { ["name"] = "a" }), "missing_image_content", "images[0].content" },
            { Body(new JObject { ["name"] = "a", ["content"] = "" }), "missing_image_content", "images[0].content" },
            { Body(new JObject { ["name"] = "a", ["content"] = 42 }), "invalid_image_content", "images[0].content" },
            { Body(new JObject { ["name"] = "a", ["content"] = "not base64!" }), "invalid_image_content", "images[0].content" },
            { Body(new JObject { ["name"] = "a", ["content"] = "data:image/png," + Convert.ToBase64String(png) }), "invalid_image_content", "images[0].content" },
            { Body(Row("a", gif)), "unsupported_image_format", "images[0].content" },
            { Body(Row("a", webp)), "unsupported_image_format", "images[0].content" },
            { Body(Row("a", png, "gif")), "unsupported_image_format", "images[0].format" },
            { Body(Row("a", png, "jpeg")), "image_format_mismatch", "images[0].format" },
            { Body(Row("a", Images.Png(400, 400, animated: true))), "animated_image", "images[0].content" },
            { Body(Row("a", Images.Png(400, 400).Take(20).ToArray())), "invalid_image", "images[0].content" },
            { Body(Row("a", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 })), "invalid_image", "images[0].content" },
            { Body(Row("a", Images.Png(279, 600))), "invalid_image_dimensions", "images[0].content" },
            { Body(Row("a", Images.Png(600, 1569))), "invalid_image_dimensions", "images[0].content" },
            { Body(Row("a", Images.Jpeg(1569, 1569))), "invalid_image_dimensions", "images[0].content" },
            { Body(Row("a", Images.Jpeg(200, 200))), "invalid_image_dimensions", "images[0].content" },
            { Body("not a row"), "invalid_image", "images[0]" },
            { WithImages("s", 42), "invalid_images", "images" },
            { WithImages("s", "{bad"), "invalid_images_json", "images" },
            { WithImages("s", new JObject { ["a"] = new JObject { ["type"] = "audio", ["data_base64"] = Convert.ToBase64String(png) } }), "invalid_asset_type", "images['a'].type" },
            { WithImages("s", new JObject { ["a"] = "x" }), "invalid_image", "images['a']" },
            { both, "conflicting_images", "assets" }
        };
    }

    [Theory]
    [MemberData(nameof(InvalidImages))]
    public async Task Invalid_images_are_rejected_without_calling_foundry(JObject body, string expectedCode, string expectedPath)
    {
        await AssertRejectedAsync(body, expectedCode, expectedPath);
    }

    [Fact]
    public async Task Dimension_errors_report_the_actual_size()
    {
        var (response, _) = await Harness.RunAsync("Evaluate", HttpMethod.Post, "/systemone", WithImages("image:a", Rows(Images.Png(200, 640), "a")).ToString(), Ok);

        Assert.Contains("200x640", (string)(await EvaluateTests.ReadJson(response))["error"]["message"]);
    }

    [Fact]
    public async Task Images_over_16_mib_are_rejected()
    {
        var large = Images.Png(400, 400, padTo: 16 * 1024 * 1024 + 1);

        await AssertRejectedAsync(WithImages("image:a", Rows(large, "a")), "image_too_large", "images[0].content");
    }

    [Fact]
    public async Task Double_encoded_text_that_is_not_an_image_is_still_rejected()
    {
        var content = Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.ASCII.GetBytes("plain text"))));

        await AssertRejectedAsync(WithImages("image:a", new JArray(new JObject { ["name"] = "a", ["content"] = content })), "unsupported_image_format", "images[0].content");
    }

    [Fact]
    public async Task Double_encoded_images_of_16_mib_are_accepted()
    {
        var image = Images.Png(400, 400, padTo: 16 * 1024 * 1024);
        var content = Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToBase64String(image)));

        var sent = await SendAsync(WithImages("image:a", new JArray(new JObject { ["name"] = "a", ["content"] = content })));

        Assert.Equal(Convert.ToBase64String(image), (string)sent["assets"]["a"]["data_base64"]);
    }

    [Fact]
    public async Task Images_of_exactly_16_mib_are_accepted()
    {
        var sent = await SendAsync(WithImages("image:a", Rows(Images.Png(400, 400, padTo: 16 * 1024 * 1024), "a")));

        Assert.Equal("png", (string)sent["assets"]["a"]["format"]);
    }

    [Fact]
    public async Task EvaluateJson_sends_the_same_assets()
    {
        var (_, context) = await Harness.RunAsync("EvaluateJson", HttpMethod.Post, "/_connector/evaluate-json", WithImages("image:a", Rows("a")).ToString(), Ok);

        Assert.Equal("png", (string)JObject.Parse(Assert.Single(context.Sent).Body)["assets"]["a"]["format"]);
    }

    [Fact]
    public async Task Output_schema_ignores_images()
    {
        var withImages = WithImages("image:a", Rows("a"));
        var (fromImages, _) = await Harness.RunAsync("GetOutputSchema", HttpMethod.Post, "/_connector/output-schema", withImages.ToString());
        var (fromQuestions, _) = await Harness.RunAsync("GetOutputSchema", HttpMethod.Post, "/_connector/output-schema", new JObject { ["questions"] = NoulQuestion }.ToString());

        Assert.True(JToken.DeepEquals(await EvaluateTests.ReadJson(fromQuestions), await EvaluateTests.ReadJson(fromImages)));
    }

    [Fact]
    public async Task Logs_do_not_contain_image_names_or_content()
    {
        var content = Convert.ToBase64String(Harness.Asset(CatPng));
        var body = WithImages("s", new JArray(new JObject { ["name"] = "secret-photo.png", ["content"] = content }));
        var (_, context) = await Harness.RunAsync("Evaluate", HttpMethod.Post, "/systemone", body.ToString(), Ok);
        var invalid = WithImages("s", new JArray(new JObject { ["name"] = "secret-photo.png", ["content"] = Convert.ToBase64String(Images.Png(10, 10)) }));
        var (_, failed) = await Harness.RunAsync("Evaluate", HttpMethod.Post, "/systemone", invalid.ToString(), Ok);

        var logs = string.Join("\n", context.CapturingLogger.Messages.Concat(failed.CapturingLogger.Messages));
        Assert.NotEmpty(context.CapturingLogger.Messages);
        Assert.DoesNotContain("secret-photo", logs);
        Assert.DoesNotContain(content.Substring(0, 40), logs);
    }

    private static JObject WithImages(string state, JToken images) => new JObject
    {
        ["state"] = state,
        ["questions"] = NoulQuestion,
        ["images"] = images
    };

    private static JArray Rows(params string[] names) =>
        new JArray(names.Select(name => new JObject { ["name"] = name, ["content"] = Convert.ToBase64String(Images.Png(400, 400)) }));

    private static JArray Rows(byte[] content, string name) =>
        new JArray(new JObject { ["name"] = name, ["content"] = Convert.ToBase64String(content) });

    private static async Task<JObject> SendAsync(JObject body)
    {
        var (response, context) = await Harness.RunAsync("Evaluate", HttpMethod.Post, "/systemone", body.ToString(), Ok);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JObject.Parse(Assert.Single(context.Sent).Body);
    }

    private static async Task AssertRejectedAsync(JObject body, string expectedCode, string expectedPath)
    {
        var (response, context) = await Harness.RunAsync("Evaluate", HttpMethod.Post, "/systemone", body.ToString(), Ok);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await EvaluateTests.ReadJson(response))["error"];
        Assert.Equal(expectedCode, (string)error["code"]);
        Assert.Equal(expectedPath, (string)error["path"]);
        Assert.Empty(context.Sent);
    }
}

// Builds minimal PNG and JPEG files whose headers carry the given size. The connector reads only
// the headers, so the pixel data does not need to be valid.
internal static class Images
{
    public static byte[] Png(int width, int height, bool animated = false, int padTo = 0)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteInt32(header, 0, width);
        WriteInt32(header, 4, height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(stream, "IHDR", header);
        if (animated)
        {
            WriteChunk(stream, "acTL", new byte[8]);
        }

        WriteChunk(stream, "IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 });
        if (padTo > 0)
        {
            var padding = padTo - (int)stream.Length - 24;
            WriteChunk(stream, "tEXt", new byte[Math.Max(0, padding)]);
        }

        WriteChunk(stream, "IEND", Array.Empty<byte>());
        return stream.ToArray();
    }

    public static byte[] Jpeg(int width, int height, byte sofMarker = 0xC0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10 });
        bytes.AddRange(Encoding.ASCII.GetBytes("JFIF\0"));
        bytes.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 });
        bytes.AddRange(new byte[] { 0xFF, 0xDB, 0x00, 0x03, 0x00 });
        bytes.AddRange(new byte[] { 0xFF, sofMarker, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03 });
        bytes.AddRange(new byte[] { 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });
        bytes.AddRange(new byte[] { 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt32(length, 0, data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(new byte[4]);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
