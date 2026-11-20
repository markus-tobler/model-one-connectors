# Using the Microsoft-Decision-1.1 connector

The connector sends a state, optional images, and a set of typed questions to a [Microsoft-Decision-1.1](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision) deployment in Microsoft Foundry. The model returns one answer per question, including probabilities that a flow can use for routing, thresholds, approvals, and other automation.

Microsoft-Decision-1.1 uses the same question types and response shape as TypeSafe's Jev, so the actions, question rows, and generated outputs work the same way as in the [Jev connector](usage.md). This guide covers what is different and repeats the essentials.

This guide uses Power Automate for the examples. The same connector actions can also be used from other supported Power Platform workflow surfaces and Copilot Studio.

## Before you start

You need:

- The **Microsoft-Decision-1** custom connector installed in your Power Platform environment. The connector keeps this name because Power Platform cannot rename an existing connector; its actions say Microsoft-Decision-1.1. Download the managed `MicrosoftDecisionCustomConnector` solution from the repository's [Releases page](https://github.com/markus-tobler/model-one-connectors/releases), then import it through Power Apps under **Solutions**.
- A Microsoft Foundry resource with a Microsoft-Decision-1.1 deployment. Follow [Deploy Microsoft-Decision-1.1](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision#deploy-microsoft-decision-11) to create one.
- From the deployment details page in the Foundry portal: the resource endpoint, the deployment name, and an API key.

## Create a connection

When Power Automate first asks for a connection, enter:

| Field | Value | Example |
| --- | --- | --- |
| **Foundry resource endpoint** | The endpoint of the Foundry resource. Only the scheme and host are used, so you can also paste the deployment's target URI. | `https://my-resource.services.ai.azure.com` |
| **Deployment name** | The name of your deployment, not the model name. | `Decision-1.1` |
| **API key** | Key 1 or Key 2 of the Foundry resource. | |

A connection always calls one deployment. To use another deployment or resource, create another connection. Store and share the connection as you would any other credential-bearing Power Platform connection.

The `*.services.ai.azure.com`, `*.cognitiveservices.azure.com`, and `*.openai.azure.com` hosts of a resource all work.

## Power Automate example

The following example evaluates a support message and decides:

- whether the message is urgent;
- which team should handle it;
- how severe the reported problem is.

1. Create or edit a cloud flow, add a new action, find **Microsoft-Decision-1**, and select **Evaluate with Microsoft-Decision-1.1**.
2. In **State**, enter the text or dynamic content to evaluate, such as an email body, support ticket, form response, or transcript. If the complete value is a JSON object or array, the connector sends it as structured JSON. Other values are sent as text.
3. In **Questions**, add one row for each question. Every row displays the fields for all question types; fill in only the fields that apply to the selected type.
4. In later actions, select the generated answers from **Dynamic content**.

| Question type | Use it to | Configuration | Returned answer |
| --- | --- | --- | --- |
| `noul` | Ask a yes/no question | Instructions and optional descriptions of what yes and no mean | Probability from 0 (no) to 1 (yes) |
| `choice` | Select one named option | Instructions and 2-255 unique options, each with an optional description | Selected option, confidence, and probability per option |
| `score` | Rate against an ordered rubric | Instructions and 2-10 level descriptions, lowest level first | Probability-weighted score, confidence, legend, and probability per level |

Use a unique **Question ID** for every row. The ID becomes the key in the API response and the prefix of the generated Power Automate fields, so use short stable names such as `is_urgent`, `category`, and `severity`.

For example:

| ID | Type | Instructions | Type-specific criteria |
| --- | --- | --- | --- |
| `is_urgent` | `noul` | Does this message need an immediate response? | Yes: Production impact or a hard deadline today. |
| `category` | `choice` | Which team should handle this? | `billing`, `engineering`, `other` |
| `severity` | `score` | How severe is the reported problem? | No impact; Minor inconvenience; Feature broken; Revenue-impacting outage |

The generated fields depend on the question type:

| Question type | Dynamic outputs |
| --- | --- |
| `noul` | `<id> yes probability` |
| `choice` | `<id> choice`, `<id> confidence`, `<id> probability <option>` |
| `score` | `<id> score`, `<id> confidence`, `<id> probability <n>`, `<id> legend <n>` |

`Model`, `Input tokens`, and `Output tokens` are always available. `Model` contains the underlying model name, such as `microsoft-decision-1.1`, not your deployment name.

For example, a condition can route urgent messages when `is_urgent yes probability` is greater than `0.8`. The equivalent expression is:

```text
@greater(body('Evaluate_with_Microsoft-Decision-1.1')?['answers']?['is_urgent']?['noul'], 0.8)
```

Replace `Evaluate_with_Microsoft-Decision-1.1` with the name of the action in your flow.

For choice and score questions, use confidence to decide whether the flow should act automatically or send the item for review. Validate thresholds against labeled examples from your own workload; see the [known limitations and best practices](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision#known-limitations-and-risks).

If you change the questions in a saved flow, open the action again so the designer refreshes its dynamic output schema.

## Evaluating images

Microsoft-Decision-1.1 can make decisions about images as well as text. Add images in the action's **Images** input, one row per image:

| Field | Value |
| --- | --- |
| **Image name** | A unique name such as `photo.png`. Use letters, digits, `.`, `_`, and `-` only, up to 64 characters. |
| **Image content** | The file content, for example **File content** from the OneDrive or SharePoint **Get file content** action, or base64 text. A `data:image/...;base64,` URI also works. |
| **Image format** | Optional, under advanced parameters. The connector detects PNG or JPEG from the content. |

Each image must be a still PNG or JPEG, 280 to 1568 pixels per side, and at most 16 MiB. The connector checks this before it calls Foundry and does not resize or convert images.

The model sees an image where the text refers to it as `image:<name>`. You can write that reference in:

- the **State**, for example `Customer photo of the delivery: image:photo.png`;
- a question's **Instructions**, for example `Does image:photo.png show damage?`;
- a choice option's description, for example to pick the best of several pictures.

If no text refers to an image, the connector appends the reference to the end of the State, so the simplest setup needs no references at all. For example, to check a delivery photo:

1. Add **Get file content** for the photo.
2. In **State**, enter `Customer photo of a delivery.`
3. In **Images**, add a row with the name `photo.png` and the **File content** of the previous step.
4. Add a `noul` question `damaged` with the instructions `Does the photo show damage?`.

The connector sends `Customer photo of a delivery.` followed by `image:photo.png`, and `damaged yes probability` is the probability that the photo shows damage.

When the State is JSON, the connector does not change it. Refer to every image in the JSON or in the questions yourself, or the action fails with `unreferenced_image`.

Large images can hit the deployment's token rate limit even though an image uses only a few hundred tokens. Foundry reserves quota based on the request size, roughly 0.25 tokens per byte, before it runs the model. With a quota of 2.25 million tokens per minute, requests above about 9 MB, or images above roughly 6.5 MB, always return 429 `RateLimitReached`. Increase the deployment's quota, or send smaller images, for example JPEG instead of PNG.

Write each reference once. The service allows at most 8 image references per question prompt and counts repeated references, so a request with many images or repeated references can fail with Foundry's `invalid_image` error. A reference that names no image is treated as plain text.

To pass images from an expression, select **Switch to input entire array** on **Images**. You can provide the rows, or the native assets object keyed by image name:

```json
{
  "photo.png": { "type": "image", "format": "png", "data_base64": "iVBORw0KGgo..." }
}
```

## Entering questions as JSON

On **Questions**, select **Switch to input entire array** to use an expression, a variable, or JSON directly. The connector accepts the same two shapes as the Jev connector:

- The connector row schema: an array of rows, where choice options are `"options": [{ "option": "billing", "description": "..." }]`.
- The native decision schema: an object keyed by question ID, where choice options are `"criteria": { "billing": "..." }`.

See [Entering questions as JSON](usage.md#entering-questions-as-json) in the Jev guide for complete examples. Both work unchanged with Microsoft-Decision-1.

When questions come from an expression, the designer cannot inspect their value while the flow is being edited. It therefore shows a generic **Answers** object instead of question-specific dynamic fields. The action still runs normally.

## Available actions

| Action | Purpose |
| --- | --- |
| **Evaluate with Microsoft-Decision-1.1** | Evaluates a state and exposes question-specific, strongly typed dynamic outputs. |
| **Evaluate with Microsoft-Decision-1.1 (JSON text)** | Performs the same evaluation and returns the native response in `responseJson`. The `apim-request-id` and `x-request-id` response headers are kept for support requests. |

Unlike the Jev connector, there is no model picker or **List models** action. The deployment is part of the connection.

## Errors and retries

Input and connection validation errors are returned by the connector before Foundry is called:

```json
{
  "error": {
    "code": "missing_choice_options",
    "message": "A choice question needs at least 2 options.",
    "path": "questions[1].options"
  }
}
```

Common codes include:

- `missing_api_key` (401), `missing_deployment`, `invalid_endpoint`
- `missing_state`, `invalid_state`
- `missing_questions`, `invalid_questions`, `invalid_questions_json`
- `missing_question_id`, `duplicate_question_id`, `invalid_question_type`
- `invalid_noul_criteria`, `missing_choice_options`, `too_many_choice_options`
- `missing_choice_key`, `duplicate_choice_key`, `invalid_score_levels`
- `missing_image_name`, `invalid_image_name`, `duplicate_image_name`, `missing_image_content`, `invalid_image_content`
- `unsupported_image_format`, `image_format_mismatch`, `animated_image`, `invalid_image`, `invalid_image_dimensions`, `image_too_large`
- `unreferenced_image`, `invalid_images`, `invalid_images_json`, `invalid_asset_type`, `conflicting_images`

Foundry errors are passed through unchanged:

| Status | Meaning |
| --- | --- |
| 400 | Foundry rejected the request, for example `unsupported_request_argument`, or `invalid_image` when a question prompt references more than 8 images. |
| 401 | The API key is invalid, or the endpoint does not belong to the key's resource. |
| 404 | The deployment name was not found (`DeploymentNotFound`). |
| 429 | The deployment's rate limit was exceeded; retry with backoff, and honor `retry-after` when present. |

The connector does not retry requests. Configure the action's retry policy or add explicit flow logic when retries are appropriate.

## Further reading

- [Deploy and use Microsoft-Decision-1.1 in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision)
- [Deployment types for Microsoft Foundry Models](https://learn.microsoft.com/azure/foundry/foundry-models/concepts/deployment-types)
