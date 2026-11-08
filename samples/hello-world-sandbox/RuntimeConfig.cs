using System.Text.Json;
using Google.Protobuf;
using Kagent.Runtime.V1;
using Lf.A2a.V1;

namespace HelloWorldSandbox;

/// <summary>
/// The subset of kagent's sandbox runtime contract this agent consumes.
/// kagent delivers the compiled ADKConfig (ProtoJSON) and the agent card in the revision's
/// bootstrap bundle (see <see cref="BootstrapLoader"/>); it does not set the legacy
/// KAGENT_URL/NAME/NAMESPACE.
/// </summary>
public sealed record RuntimeConfig(
    string ControlPlaneUrl,
    int A2APort,
    string AgentName,
    string Model,
    string? OpenAIBaseUrl,
    string? Instruction,
    AgentCard AgentCard)
{
    // Fixed by the sandbox: Substrate probes readiness here before snapshotting the golden actor.
    public const int ReadinessPort = 8081;

    public static RuntimeConfig FromBundle(BootstrapBundle bundle)
    {
        using var config = JsonDocument.Parse(bundle.ConfigJson.Memory);
        var root = config.RootElement;

        var runtime = root.GetProperty("runtime");
        var controlPlaneUrl = GetString(runtime, "control_plane_url", "controlPlaneUrl")
            ?? throw new InvalidOperationException("runtime.control_plane_url is missing from the bootstrap config.");
        var port = 80;
        if (TryGetProperty(runtime, out var portElement, "port"))
        {
            port = portElement.ValueKind == JsonValueKind.String
                ? int.Parse(portElement.GetString()!)
                : portElement.GetInt32();
        }

        string? model = null, baseUrl = null, instruction = null;
        if (TryGetProperty(root, out var agent, "agent"))
        {
            instruction = GetString(agent, "instruction");
            if (TryGetProperty(agent, out var modelConfig, "model"))
            {
                model = GetString(modelConfig, "model");
                if (TryGetProperty(modelConfig, out var openai, "openai"))
                {
                    baseUrl = GetString(openai, "base_url", "baseUrl");
                }
            }
        }

        var cardJson = bundle.AgentCardJson.ToStringUtf8();
        var card = string.IsNullOrEmpty(cardJson)
            ? new AgentCard()
            : new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true)).Parse<AgentCard>(cardJson);

        return new RuntimeConfig(
            ControlPlaneUrl: controlPlaneUrl,
            A2APort: port,
            AgentName: GetString(runtime, "name") ?? card.Name,
            Model: string.IsNullOrEmpty(model)
                ? throw new InvalidOperationException("agent.model.model is missing from the bootstrap config; set a ModelConfig on the AgentTemplate.")
                : model,
            OpenAIBaseUrl: string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
            Instruction: string.IsNullOrEmpty(instruction) ? null : instruction,
            AgentCard: card);
    }

    // ProtoJSON emitters may use either the proto field name or its lowerCamelCase JSON name.
    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            {
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names) =>
        TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
