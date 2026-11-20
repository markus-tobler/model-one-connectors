public class Script : ScriptBase
{
    private const string EvaluateOperation = "Evaluate";
    private const string EvaluateJsonOperation = "EvaluateJson";
    private const string GetOutputSchemaOperation = "GetOutputSchema";

    private const string SystemOnePath = "/providers/microsoft/v1/systemone";
    private const string ApiKeyHeader = "api-key";

    // Set from the connection's deployment name by a setheader policy; never forwarded to Foundry.
    private const string DeploymentHeader = "x-connector-deployment";

    private const int MinChoiceOptions = 2;
    private const int MaxChoiceOptions = 255;
    private const int MinScoreLevels = 2;
    private const int MaxScoreLevels = 10;

    // Image limits of the decision API: still PNG or JPEG, 280 to 1568 pixels per side, 16 MiB each.
    private const int MinImageSide = 280;
    private const int MaxImageSide = 1568;
    private const int MaxImageBytes = 16 * 1024 * 1024;
    private const int MaxImageNameLength = 64;
    private const string ImageReferencePrefix = "image:";

    private static readonly string[] KnownOperations =
    {
        EvaluateOperation,
        EvaluateJsonOperation,
        GetOutputSchemaOperation
    };

    private static readonly string[] RequestIdHeaders =
    {
        "apim-request-id",
        "x-request-id"
    };

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        var operationId = ResolveOperationId(this.Context.OperationId);
        this.Context.Logger.LogInformation("Microsoft-Decision-1.1 connector operation {operationId}", operationId ?? "unknown");

        try
        {
            switch (operationId)
            {
                case EvaluateOperation:
                    return await this.EvaluateAsync(false).ConfigureAwait(false);
                case EvaluateJsonOperation:
                    return await this.EvaluateAsync(true).ConfigureAwait(false);
                case GetOutputSchemaOperation:
                    return await this.GetOutputSchemaAsync().ConfigureAwait(false);
                default:
                    throw new ConnectorException(HttpStatusCode.BadRequest, "unknown_operation", "The connector does not support this operation.", null);
            }
        }
        catch (ConnectorException ex)
        {
            this.Context.Logger.LogWarning("Microsoft-Decision-1.1 connector rejected request: {code} at {path}", ex.Code, ex.Path ?? string.Empty);
            return CreateError(ex.StatusCode, ex.Code, ex.Message, ex.Path);
        }
    }

    private async Task<HttpResponseMessage> EvaluateAsync(bool wrapAsJson)
    {
        var original = this.Context.Request;
        var apiKey = ReadApiKey(original);
        var deployment = ReadDeployment(original);
        var endpoint = BuildSystemOneUri(original.RequestUri);

        var body = await this.ReadJsonBodyAsync().ConfigureAwait(false);
        var payload = BuildSystemOnePayload(body, deployment);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = CreateJsonContent(payload.ToString(Newtonsoft.Json.Formatting.None));

        var upstream = await this.Context.SendAsync(request, this.CancellationToken).ConfigureAwait(false);
        this.Context.Logger.LogInformation("Microsoft-Decision-1.1 evaluation returned {status}", (int)upstream.StatusCode);

        if (!wrapAsJson || !upstream.IsSuccessStatusCode)
        {
            return upstream;
        }

        var responseText = upstream.Content == null
            ? string.Empty
            : await upstream.Content.ReadAsStringAsync().ConfigureAwait(false);

        var wrapped = new HttpResponseMessage(upstream.StatusCode)
        {
            Content = CreateJsonContent(new JObject { ["responseJson"] = responseText }.ToString(Newtonsoft.Json.Formatting.None))
        };

        foreach (var header in RequestIdHeaders)
        {
            IEnumerable<string> values;
            if (upstream.Headers.TryGetValues(header, out values))
            {
                wrapped.Headers.TryAddWithoutValidation(header, values);
            }
        }

        return wrapped;
    }

    private async Task<HttpResponseMessage> GetOutputSchemaAsync()
    {
        JObject schema;
        try
        {
            var body = await this.ReadJsonBodyAsync().ConfigureAwait(false);
            schema = BuildResponseSchema(NormalizeQuestions(body["questions"], "questions"));
        }
        catch (ConnectorException ex)
        {
            // Design-time callers get a usable generic schema instead of a blocking error; the
            // same questions are rejected with details when the action actually runs.
            schema = BuildGenericResponseSchema(ex.Message);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateJsonContent(new JObject { ["schema"] = schema }.ToString(Newtonsoft.Json.Formatting.None))
        };
    }

    private async Task<JObject> ReadJsonBodyAsync()
    {
        var content = this.Context.Request.Content;
        var text = content == null ? string.Empty : await content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_body", "The request body is required.", null);
        }

        JToken token;
        try
        {
            token = ParseJson(text);
        }
        catch (JsonException)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_body", "The request body must be a JSON object.", null);
        }

        var body = token as JObject;
        if (body == null)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_body", "The request body must be a JSON object.", null);
        }

        return body;
    }

    internal static string ResolveOperationId(string operationId)
    {
        if (string.IsNullOrEmpty(operationId) || KnownOperations.Contains(operationId))
        {
            return operationId;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(operationId));
            if (KnownOperations.Contains(decoded))
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
        }

        return operationId;
    }

    internal static string ReadApiKey(HttpRequestMessage request)
    {
        var apiKey = ReadHeader(request, ApiKeyHeader);
        if (apiKey.Length == 0)
        {
            throw new ConnectorException(HttpStatusCode.Unauthorized, "missing_api_key", "The connection does not provide a Foundry API key.", null);
        }

        return apiKey;
    }

    internal static string ReadDeployment(HttpRequestMessage request)
    {
        var deployment = ReadHeader(request, DeploymentHeader);
        if (deployment.Length == 0)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_deployment", "The connection does not provide a deployment name.", null);
        }

        return deployment;
    }

    private static string ReadHeader(HttpRequestMessage request, string name)
    {
        IEnumerable<string> values;
        var raw = request.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;
        return raw == null ? string.Empty : raw.Trim();
    }

    // The host policy routes the request to the connection's endpoint. Only its scheme, host, and
    // port are kept, so an endpoint that was pasted with a path still reaches the decision API.
    internal static Uri BuildSystemOneUri(Uri requestUri)
    {
        if (requestUri == null || !requestUri.IsAbsoluteUri || requestUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_endpoint", "The connection's Foundry resource endpoint must be an HTTPS URL.", null);
        }

        return new UriBuilder(requestUri.Scheme, requestUri.Host, requestUri.Port, SystemOnePath).Uri;
    }

    internal static JObject BuildSystemOnePayload(JObject body, string deployment)
    {
        var state = NormalizeState(body);
        var questions = NormalizeQuestions(body["questions"], "questions");
        var payload = new JObject
        {
            ["model"] = deployment,
            ["state"] = state,
            ["questions"] = questions
        };

        Dictionary<string, string> imagePaths;
        var assets = NormalizeImages(body, out imagePaths);
        if (assets != null)
        {
            payload["state"] = ReferenceImages(state, questions, imagePaths);
            payload["assets"] = assets;
        }

        return payload;
    }

    // Images arrive as connector rows (images), as a native assets object (images or assets), or
    // as JSON text of either. Each is validated locally and sent as a native image asset.
    internal static JObject NormalizeImages(JObject body, out Dictionary<string, string> imagePaths)
    {
        imagePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = body["images"];
        var assets = body["assets"];
        var path = "images";
        if (IsEmptyImages(images))
        {
            images = assets;
            path = "assets";
        }
        else if (!IsEmptyImages(assets))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "conflicting_images", "Provide images or assets, not both.", "assets");
        }

        if (IsEmptyImages(images))
        {
            return null;
        }

        if (images.Type == JTokenType.String)
        {
            try
            {
                images = ParseJson((string)images);
            }
            catch (JsonException)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_images_json", "The images text is not valid JSON.", path);
            }

            if (IsEmptyImages(images))
            {
                return null;
            }
        }

        var normalized = new JObject();
        if (images.Type == JTokenType.Array)
        {
            var rows = (JArray)images;
            for (var index = 0; index < rows.Count; index++)
            {
                var rowPath = path + "[" + index + "]";
                var row = rows[index] as JObject;
                if (row == null)
                {
                    throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_image", "Each image must be an object with a name and content.", rowPath);
                }

                var name = ReadOptionalString(row["name"], rowPath + ".name");
                AddImage(normalized, imagePaths, name, row["content"], ReadOptionalString(row["format"], rowPath + ".format"), rowPath, rowPath + ".content");
            }
        }
        else if (images.Type == JTokenType.Object)
        {
            foreach (var property in ((JObject)images).Properties())
            {
                var assetPath = path + "['" + property.Name + "']";
                var asset = property.Value as JObject;
                if (asset == null)
                {
                    throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_image", "Each asset must be an object with type, format, and data_base64.", assetPath);
                }

                var type = ReadOptionalString(asset["type"], assetPath + ".type");
                if (type != null && type != "image")
                {
                    throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_asset_type", "Only image assets are supported.", assetPath + ".type");
                }

                AddImage(normalized, imagePaths, property.Name, asset["data_base64"], ReadOptionalString(asset["format"], assetPath + ".format"), assetPath, assetPath + ".data_base64");
            }
        }
        else
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_images", "Images must be a list of image rows or an assets object keyed by image name.", path);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static bool IsEmptyImages(JToken images)
    {
        return images == null
            || images.Type == JTokenType.Null
            || images.Type == JTokenType.Undefined
            || (images.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)images))
            || (images.Type == JTokenType.Array && ((JArray)images).Count == 0)
            || (images.Type == JTokenType.Object && ((JObject)images).Count == 0);
    }

    private static void AddImage(JObject assets, Dictionary<string, string> imagePaths, string name, JToken content, string format, string path, string contentPath)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_image_name", "Each image needs a name.", path + ".name");
        }

        if (name.Length > MaxImageNameLength || !name.All(IsImageNameChar))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_image_name", "Image names may contain only letters, digits, '.', '_', and '-', with at most " + MaxImageNameLength + " characters.", path + ".name");
        }

        if (assets.Property(name) != null)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "duplicate_image_name", "Image name '" + name + "' is used more than once.", path + ".name");
        }

        var data = DecodeImageContent(content, contentPath);
        int width;
        int height;
        var detected = InspectImage(data, contentPath, out width, out height);

        if (!string.IsNullOrWhiteSpace(format))
        {
            var requested = NormalizeImageFormat(format);
            if (requested == null)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "unsupported_image_format", "Images must be PNG or JPEG.", path + ".format");
            }

            if (requested != detected)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "image_format_mismatch", "The image content is " + detected.ToUpperInvariant() + ", not " + requested.ToUpperInvariant() + ".", path + ".format");
            }
        }

        if (width < MinImageSide || height < MinImageSide || width > MaxImageSide || height > MaxImageSide)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_image_dimensions", "The image is " + width + "x" + height + " pixels; each side must be between " + MinImageSide + " and " + MaxImageSide + " pixels.", contentPath);
        }

        assets[name] = new JObject
        {
            ["type"] = "image",
            ["format"] = detected,
            ["data_base64"] = Convert.ToBase64String(data)
        };
        imagePaths[name] = path;
    }

    private static string NormalizeImageFormat(string format)
    {
        switch (format.Trim().ToLowerInvariant())
        {
            case "png":
            case "image/png":
                return "png";
            case "jpeg":
            case "jpg":
            case "image/jpeg":
                return "jpeg";
            default:
                return null;
        }
    }

    // Accepts base64 text, a data URI, or a Power Automate file object ({"$content-type", "$content"}).
    internal static byte[] DecodeImageContent(JToken content, string path)
    {
        var file = content as JObject;
        if (file != null)
        {
            content = file["$content"];
        }

        if (content == null || content.Type == JTokenType.Null || content.Type == JTokenType.Undefined)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_image_content", "Each image needs content.", path);
        }

        if (content.Type != JTokenType.String)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_image_content", "The image content must be base64 text or file content.", path);
        }

        var text = StripDataUri(((string)content).Trim(), path);
        if (text.Length == 0)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_image_content", "Each image needs content.", path);
        }

        // Reject oversized input before decoding it. The slack allows whitespace and the second
        // base64 layer described below.
        if (text.Length > 2L * (((long)MaxImageBytes + 2) / 3) * 4)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "image_too_large", "Each image may be at most 16 MiB.", path);
        }

        var data = DecodeBase64(text, path);

        // Power Automate base64-encodes text that a flow passes to a byte parameter, so base64 text
        // from an expression arrives encoded twice. Undo the second layer when it hides an image.
        if (!IsSupportedImage(data) && data.All(value => value < 0x80))
        {
            try
            {
                var nested = DecodeBase64(StripDataUri(Encoding.ASCII.GetString(data).Trim(), path), path);
                if (IsSupportedImage(nested))
                {
                    data = nested;
                }
            }
            catch (ConnectorException)
            {
            }
        }

        if (data.Length > MaxImageBytes)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "image_too_large", "Each image may be at most 16 MiB.", path);
        }

        return data;
    }

    private static string StripDataUri(string text, string path)
    {
        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var comma = text.IndexOf(',');
        if (comma < 0 || text.LastIndexOf(";base64", comma, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_image_content", "A data URI image must be base64 encoded.", path);
        }

        return text.Substring(comma + 1);
    }

    private static byte[] DecodeBase64(string text, string path)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_image_content", "The image content is not valid base64.", path);
        }
    }

    private static bool IsSupportedImage(byte[] data)
    {
        return (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            || (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF);
    }

    // Reads the format and pixel size from the PNG or JPEG header without decoding the image.
    internal static string InspectImage(byte[] data, string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            InspectPng(data, path, out width, out height);
            return "png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            InspectJpeg(data, path, out width, out height);
            return "jpeg";
        }

        throw new ConnectorException(HttpStatusCode.BadRequest, "unsupported_image_format", "Images must be PNG or JPEG.", path);
    }

    private static void InspectPng(byte[] data, string path, out int width, out int height)
    {
        if (data.Length < 33 || ReadAscii(data, 12, 4) != "IHDR")
        {
            throw InvalidImage(path);
        }

        width = ReadInt32BigEndian(data, 16);
        height = ReadInt32BigEndian(data, 20);

        // An acTL chunk before the first IDAT marks an animated PNG.
        long offset = 8;
        while (offset + 8 <= data.Length)
        {
            var length = (uint)ReadInt32BigEndian(data, (int)offset);
            var type = ReadAscii(data, (int)offset + 4, 4);
            if (type == "acTL")
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "animated_image", "Animated images are not supported; use a still PNG or JPEG.", path);
            }

            if (type == "IDAT" || type == "IEND")
            {
                return;
            }

            offset += 12L + length;
        }
    }

    private static void InspectJpeg(byte[] data, string path, out int width, out int height)
    {
        var position = 2;
        while (position < data.Length)
        {
            if (data[position] != 0xFF)
            {
                throw InvalidImage(path);
            }

            while (position < data.Length && data[position] == 0xFF)
            {
                position++;
            }

            if (position >= data.Length)
            {
                break;
            }

            var marker = data[position];
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                position++;
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA || position + 2 >= data.Length)
            {
                break;
            }

            var length = (data[position + 1] << 8) | data[position + 2];
            if (length < 2)
            {
                break;
            }

            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (length < 7 || position + 7 >= data.Length)
                {
                    break;
                }

                height = (data[position + 4] << 8) | data[position + 5];
                width = (data[position + 6] << 8) | data[position + 7];
                return;
            }

            position += 1 + length;
        }

        throw InvalidImage(path);
    }

    private static ConnectorException InvalidImage(string path)
    {
        return new ConnectorException(HttpStatusCode.BadRequest, "invalid_image", "The image header could not be read.", path);
    }

    private static int ReadInt32BigEndian(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static string ReadAscii(byte[] data, int offset, int count)
    {
        return offset + count <= data.Length ? Encoding.ASCII.GetString(data, offset, count) : string.Empty;
    }

    private static bool IsImageNameChar(char value)
    {
        return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || (value >= '0' && value <= '9') || value == '.' || value == '_' || value == '-';
    }

    // Every image must be referenced as image:<name>. Images that no text references are appended
    // to text state, because the decision API rejects requests that reference only some images.
    internal static JToken ReferenceImages(JToken state, JObject questions, Dictionary<string, string> imagePaths)
    {
        var texts = new List<string>();
        CollectStrings(state, texts);
        CollectStrings(questions, texts);

        var missing = imagePaths.Keys.Where(name => !texts.Any(text => ContainsImageReference(text, name))).ToList();
        if (missing.Count == 0)
        {
            return state;
        }

        if (state != null && (state.Type == JTokenType.Object || state.Type == JTokenType.Array))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "unreferenced_image", "Reference image '" + missing[0] + "' as image:" + missing[0] + " in the state, the instructions, or the criteria. JSON state is not changed automatically.", imagePaths[missing[0]]);
        }

        var references = string.Join(" ", missing.Select(name => ImageReferencePrefix + name));
        var text = state == null || state.Type != JTokenType.String ? string.Empty : (string)state;
        return new JValue(string.IsNullOrWhiteSpace(text) ? references : text + "\n\n" + references);
    }

    private static void CollectStrings(JToken token, List<string> texts)
    {
        if (token == null)
        {
            return;
        }

        if (token.Type == JTokenType.String)
        {
            texts.Add((string)token);
            return;
        }

        foreach (var child in token.Children())
        {
            CollectStrings(child is JProperty ? ((JProperty)child).Value : child, texts);
        }
    }

    // image:<name> counts when the name is not followed by another name character. A trailing
    // period, as at the end of a sentence, still counts.
    internal static bool ContainsImageReference(string text, string name)
    {
        var token = ImageReferencePrefix + name;
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var end = index + token.Length;
            if (end == text.Length
                || !IsImageNameChar(text[end])
                || (text[end] == '.' && (end + 1 == text.Length || !IsImageNameChar(text[end + 1]))))
            {
                return true;
            }

            index = text.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static JToken NormalizeState(JObject body)
    {
        JToken state;
        if (!body.TryGetValue("state", out state))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_state", "The state to evaluate is required.", "state");
        }

        if (!IsEntryValue(state))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_state", "The state must be text, a JSON object, a JSON array, or null.", "state");
        }

        if (state.Type == JTokenType.String)
        {
            var structured = TryParseStructuredJson((string)state);
            if (structured != null)
            {
                return structured;
            }
        }

        return state.DeepClone();
    }

    // Text that is a complete JSON object or array is sent as structured JSON so questions can
    // refer to its fields; anything else, including JSON scalars, stays plain text.
    private static JToken TryParseStructuredJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            var token = ParseJson(trimmed);
            return token.Type == JTokenType.Object || token.Type == JTokenType.Array ? token : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JObject NormalizeQuestions(JToken questions, string path)
    {
        if (questions == null || questions.Type == JTokenType.Null || (questions.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)questions)))
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_questions", "Add at least one question.", path);
        }

        if (questions.Type == JTokenType.String)
        {
            try
            {
                questions = ParseJson((string)questions);
            }
            catch (JsonException)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_questions_json", "The questions text is not valid JSON.", path);
            }
        }

        if (questions.Type == JTokenType.Array)
        {
            return ValidateNativeQuestions(BuildQuestionsFromRows((JArray)questions, path), path);
        }

        if (questions.Type == JTokenType.Object)
        {
            return ValidateNativeQuestions((JObject)questions, path);
        }

        throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_questions", "Questions must be a list of question rows or a questions object keyed by question ID.", path);
    }

    private static JObject BuildQuestionsFromRows(JArray rows, string path)
    {
        if (rows.Count == 0)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_questions", "Add at least one question.", path);
        }

        var questions = new JObject();
        for (var index = 0; index < rows.Count; index++)
        {
            var rowPath = path + "[" + index + "]";
            var row = rows[index] as JObject;
            if (row == null)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_question", "Each question must be an object.", rowPath);
            }

            var id = ReadOptionalString(row["id"], rowPath + ".id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "missing_question_id", "Each question needs an ID.", rowPath + ".id");
            }

            if (questions.Property(id) != null)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "duplicate_question_id", "Question ID '" + id + "' is used more than once.", rowPath + ".id");
            }

            var type = (ReadOptionalString(row["type"], rowPath + ".type") ?? string.Empty).Trim().ToLowerInvariant();
            var question = new JObject { ["type"] = type };

            var instructions = ReadOptionalString(row["instructions"], rowPath + ".instructions");
            if (!string.IsNullOrEmpty(instructions))
            {
                question["instructions"] = instructions;
            }

            switch (type)
            {
                case "noul":
                    var whenTrue = ReadOptionalString(row["noulTrue"], rowPath + ".noulTrue");
                    var whenFalse = ReadOptionalString(row["noulFalse"], rowPath + ".noulFalse");
                    if (!string.IsNullOrEmpty(whenTrue) || !string.IsNullOrEmpty(whenFalse))
                    {
                        question["criteria"] = new JObject
                        {
                            ["true"] = string.IsNullOrEmpty(whenTrue) ? JValue.CreateNull() : new JValue(whenTrue),
                            ["false"] = string.IsNullOrEmpty(whenFalse) ? JValue.CreateNull() : new JValue(whenFalse)
                        };
                    }

                    break;
                case "choice":
                    question["criteria"] = BuildChoiceCriteria(row["options"], rowPath + ".options");
                    break;
                case "score":
                    question["criteria"] = BuildScoreCriteria(row["levels"], rowPath + ".levels");
                    break;
                default:
                    throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_question_type", "The question type must be noul, choice, or score.", rowPath + ".type");
            }

            questions[id] = question;
        }

        return questions;
    }

    private static JObject BuildChoiceCriteria(JToken token, string path)
    {
        var options = token as JArray;
        if (options == null || options.Count < MinChoiceOptions)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_choice_options", "A choice question needs at least " + MinChoiceOptions + " options.", path);
        }

        if (options.Count > MaxChoiceOptions)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "too_many_choice_options", "A choice question supports at most " + MaxChoiceOptions + " options.", path);
        }

        var criteria = new JObject();
        for (var index = 0; index < options.Count; index++)
        {
            var optionPath = path + "[" + index + "]";
            var option = options[index] as JObject;
            if (option == null)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_choice_option", "Each choice option must be an object with an option key and an optional description.", optionPath);
            }

            // The row property is "option", not "key": the Power Automate array editor hides item properties named "key".
            var key = ReadOptionalString(option["option"], optionPath + ".option");
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "missing_choice_key", "Each choice option needs a key.", optionPath + ".option");
            }

            if (criteria.Property(key) != null)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "duplicate_choice_key", "Choice option '" + key + "' is used more than once.", optionPath + ".option");
            }

            var description = ReadOptionalString(option["description"], optionPath + ".description");
            criteria[key] = string.IsNullOrEmpty(description) ? JValue.CreateNull() : new JValue(description);
        }

        return criteria;
    }

    private static JArray BuildScoreCriteria(JToken token, string path)
    {
        var levels = token as JArray;
        if (levels == null || levels.Count < MinScoreLevels || levels.Count > MaxScoreLevels)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_score_levels", "A score question needs between " + MinScoreLevels + " and " + MaxScoreLevels + " levels.", path);
        }

        var criteria = new JArray();
        for (var index = 0; index < levels.Count; index++)
        {
            var text = ReadOptionalString(levels[index], path + "[" + index + "]");
            criteria.Add(string.IsNullOrEmpty(text) ? JValue.CreateNull() : new JValue(text));
        }

        return criteria;
    }

    internal static JObject ValidateNativeQuestions(JObject questions, string path)
    {
        if (questions.Count == 0)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_questions", "Add at least one question.", path);
        }

        var normalized = new JObject();
        foreach (var property in questions.Properties())
        {
            var questionPath = path + "['" + property.Name + "']";
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "missing_question_id", "Question IDs must not be empty.", questionPath);
            }

            var question = property.Value as JObject;
            if (question == null)
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_question", "Each question must be an object.", questionPath);
            }

            JToken instructions;
            if (question.TryGetValue("instructions", out instructions) && !IsEntryValue(instructions))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_instructions", "Instructions must be text, a JSON object, a JSON array, or null.", questionPath + ".instructions");
            }

            var typeToken = question["type"];
            var type = typeToken != null && typeToken.Type == JTokenType.String ? (string)typeToken : null;
            var criteria = question["criteria"];
            switch (type)
            {
                case "noul":
                    ValidateNoulCriteria(criteria, questionPath + ".criteria");
                    break;
                case "choice":
                    ValidateChoiceCriteria(criteria, questionPath + ".criteria");
                    break;
                case "score":
                    ValidateScoreCriteria(criteria, questionPath + ".criteria");
                    break;
                default:
                    throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_question_type", "The question type must be noul, choice, or score.", questionPath + ".type");
            }

            normalized[property.Name] = question.DeepClone();
        }

        return normalized;
    }

    private static void ValidateNoulCriteria(JToken criteria, string path)
    {
        if (criteria == null || criteria.Type == JTokenType.Null)
        {
            return;
        }

        var criteriaObject = criteria as JObject;
        if (criteriaObject == null)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_noul_criteria", "Noul criteria must be an object with true and false descriptions.", path);
        }

        foreach (var property in criteriaObject.Properties())
        {
            if (property.Name != "true" && property.Name != "false")
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_noul_criteria", "Noul criteria only support the keys true and false.", path + "." + property.Name);
            }

            if (!IsEntryValue(property.Value))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_criteria_description", "Criteria descriptions must be text, a JSON object, a JSON array, or null.", path + "." + property.Name);
            }
        }
    }

    private static void ValidateChoiceCriteria(JToken criteria, string path)
    {
        var criteriaObject = criteria as JObject;
        if (criteriaObject == null || criteriaObject.Count < MinChoiceOptions)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "missing_choice_options", "A choice question needs at least " + MinChoiceOptions + " options.", path);
        }

        if (criteriaObject.Count > MaxChoiceOptions)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "too_many_choice_options", "A choice question supports at most " + MaxChoiceOptions + " options.", path);
        }

        foreach (var property in criteriaObject.Properties())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "missing_choice_key", "Choice option keys must not be empty.", path);
            }

            if (!IsEntryValue(property.Value))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_criteria_description", "Criteria descriptions must be text, a JSON object, a JSON array, or null.", path + "['" + property.Name + "']");
            }
        }
    }

    private static void ValidateScoreCriteria(JToken criteria, string path)
    {
        var levels = criteria as JArray;
        if (levels == null || levels.Count < MinScoreLevels || levels.Count > MaxScoreLevels)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_score_levels", "A score question needs between " + MinScoreLevels + " and " + MaxScoreLevels + " levels.", path);
        }

        for (var index = 0; index < levels.Count; index++)
        {
            if (!IsEntryValue(levels[index]))
            {
                throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_criteria_description", "Criteria descriptions must be text, a JSON object, a JSON array, or null.", path + "[" + index + "]");
            }
        }
    }

    internal static JObject BuildResponseSchema(JObject questions)
    {
        var answers = new JObject();
        foreach (var property in questions.Properties())
        {
            answers[property.Name] = BuildAnswerSchema(property.Name, (JObject)property.Value);
        }

        return BuildEnvelopeSchema(new JObject
        {
            ["type"] = "object",
            ["description"] = "Answers keyed by question ID.",
            ["properties"] = answers
        });
    }

    internal static JObject BuildGenericResponseSchema(string reason)
    {
        var description = "Answers keyed by question ID. Named outputs appear once the questions are valid";
        description += string.IsNullOrEmpty(reason) ? "." : ": " + reason;
        return BuildEnvelopeSchema(new JObject
        {
            ["type"] = "object",
            ["description"] = description
        });
    }

    // The designer names each output token by joining the x-ms-summary of every enclosing object
    // with the token's own summary. Containers therefore carry no summary (answers, usage) or only
    // their own segment (question ID, probability, legend), and leaves carry only the last segment,
    // which yields names such as "category probability billing" without repeated words.
    private static JObject BuildEnvelopeSchema(JObject answers)
    {
        return new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["model"] = Property("string", "Model", "The model that answered the request."),
                ["answers"] = answers,
                ["usage"] = ObjectSchema(null, "Token usage for the request.", new JObject
                {
                    ["input_tokens"] = Property("integer", "Input tokens", "Number of input tokens used."),
                    ["output_tokens"] = Property("integer", "Output tokens", "Number of output tokens used.")
                })
            }
        };
    }

    private static JObject BuildAnswerSchema(string id, JObject question)
    {
        var type = (string)question["type"];
        var properties = new JObject
        {
            ["type"] = Property("string", "type", "The answer type.")
        };

        if (type == "noul")
        {
            properties["noul"] = Property("number", "yes probability", "Probability of a yes answer, from 0 to 1.");
        }
        else if (type == "choice")
        {
            var probabilities = new JObject();
            foreach (var option in ((JObject)question["criteria"]).Properties())
            {
                probabilities[option.Name] = Property("number", option.Name, "Probability of option " + option.Name + ".");
            }

            properties["choice"] = Property("string", "choice", "The selected option key.");
            properties["confidence"] = Property("number", "confidence", "Reported confidence in the selected option.");
            properties["probabilities"] = ObjectSchema("probability", "Probabilities keyed by option.", probabilities);
        }
        else
        {
            var criteria = (JArray)question["criteria"];
            var legend = new JObject();
            var probabilities = new JObject();
            for (var index = 0; index < criteria.Count; index++)
            {
                var key = Convert.ToString(index);
                var legendSchema = new JObject
                {
                    ["x-ms-summary"] = key,
                    ["description"] = "Rubric description for score " + key + "."
                };
                if (criteria[index].Type == JTokenType.String)
                {
                    legendSchema["type"] = "string";
                }

                legend[key] = legendSchema;
                probabilities[key] = Property("number", key, "Probability of score " + key + ".");
            }

            properties["score"] = Property("number", "score", "Expected score, which may fall between rubric levels.");
            properties["confidence"] = Property("number", "confidence", "Reported confidence in the score.");
            properties["legend"] = ObjectSchema("legend", "Rubric descriptions keyed by score.", legend);
            properties["probabilities"] = ObjectSchema("probability", "Probabilities keyed by score.", probabilities);
        }

        return ObjectSchema(id, "Answer for question " + id + ".", properties);
    }

    private static JObject ObjectSchema(string summary, string description, JObject properties)
    {
        var schema = new JObject { ["type"] = "object" };
        if (summary != null)
        {
            schema["x-ms-summary"] = summary;
        }

        schema["description"] = description;
        schema["properties"] = properties;
        return schema;
    }

    private static JObject Property(string type, string summary, string description)
    {
        var schema = new JObject
        {
            ["type"] = type,
            ["x-ms-summary"] = summary,
            ["description"] = description
        };
        if (type == "number")
        {
            schema["format"] = "double";
        }
        else if (type == "integer")
        {
            schema["format"] = "int64";
        }

        return schema;
    }

    private static string ReadOptionalString(JToken token, string path)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
        {
            return null;
        }

        if (token.Type != JTokenType.String)
        {
            throw new ConnectorException(HttpStatusCode.BadRequest, "invalid_value", "The value must be text.", path);
        }

        return (string)token;
    }

    private static bool IsEntryValue(JToken token)
    {
        return token == null
            || token.Type == JTokenType.Null
            || token.Type == JTokenType.String
            || token.Type == JTokenType.Object
            || token.Type == JTokenType.Array;
    }

    private static JToken ParseJson(string text)
    {
        using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
        {
            var token = JToken.ReadFrom(reader);
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                {
                    throw new JsonReaderException("Unexpected content after the JSON value.");
                }
            }

            return token;
        }
    }

    private static HttpResponseMessage CreateError(HttpStatusCode status, string code, string message, string path)
    {
        var error = new JObject
        {
            ["code"] = code,
            ["message"] = message
        };
        if (!string.IsNullOrEmpty(path))
        {
            error["path"] = path;
        }

        return new HttpResponseMessage(status)
        {
            Content = CreateJsonContent(new JObject { ["error"] = error }.ToString(Newtonsoft.Json.Formatting.None))
        };
    }

    internal class ConnectorException : Exception
    {
        public ConnectorException(HttpStatusCode statusCode, string code, string message, string path)
            : base(message)
        {
            this.StatusCode = statusCode;
            this.Code = code;
            this.Path = path;
        }

        public HttpStatusCode StatusCode { get; private set; }

        public string Code { get; private set; }

        public string Path { get; private set; }
    }
}
