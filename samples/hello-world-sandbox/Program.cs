using System.ClientModel;
using HelloWorldSandbox;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;
using OpenAI;

// kagent (v1.0 sandbox Harness, spec.byo) compiles the Agent/AgentTemplate/ModelConfig into a
// bootstrap bundle; there are no AZURE_* or KAGENT_URL variables in the sandbox. Load it before
// the listeners bind so the golden snapshot only becomes ready with its configuration saved.
var config = RuntimeConfig.FromBundle(await BootstrapLoader.LoadAsync());

// The sandbox only holds a placeholder key. kagent's egress gateway swaps in the ModelConfig's
// real key, and only for the provider host the egress policy allows (api.openai.com unless
// the ModelConfig sets a base URL), so the client must call that host directly.
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY is not set; reference an OpenAI ModelConfig from the AgentTemplate.");
var openAIOptions = new OpenAIClientOptions();
if (config.OpenAIBaseUrl != null)
{
    openAIOptions.Endpoint = new Uri(config.OpenAIBaseUrl);
}

// Create Microsoft.Agents.AI agent using ChatClientBuilder
AIAgent agent = new ChatClientBuilder(
        new OpenAIClient(new ApiKeyCredential(apiKey), openAIOptions)
        .GetChatClient(config.Model)
        .AsIChatClient())
    .BuildAIAgent(
        instructions: config.Instruction ?? "You answer to any generic question, keep in mind you are good at telling jokes.",
        name: "Joker",
        tools: null);

var builder = WebApplication.CreateBuilder(args);

// The sandbox reaches A2A over private gRPC (h2c) on runtime.port and probes readiness on 8081.
// Both listeners bind before the app reports ready, so the golden snapshot contains them.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(config.A2APort, listen => listen.Protocols = HttpProtocols.Http2);
    kestrel.ListenAnyIP(RuntimeConfig.ReadinessPort, listen => listen.Protocols = HttpProtocols.Http1);
});

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 16 << 20;
    options.MaxSendMessageSize = 16 << 20;
});
builder.Services.AddGrpcHealthChecks();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(agent);
builder.Services.AddSingleton(new TaskStoreClient(config.ControlPlaneUrl));
builder.Services.AddSingleton<AgentRunner>();

var app = builder.Build();

// Not host-filtered: the controller's :authority depends on Substrate routing. gRPC can only
// be served by the HTTP/2 listener, and Substrate only probes /readyz on the readiness port.
app.MapGrpcService<SandboxA2AService>();
app.MapGrpcHealthChecksService();
app.MapGet("/readyz", () => Results.Ok());

app.Logger.LogInformation(
    "Hello World MAF agent '{Agent}' serving A2A gRPC on :{Port}, readiness on :{ReadyPort}; model {Model}, TaskStore {ControlPlane}",
    config.AgentName, config.A2APort, RuntimeConfig.ReadinessPort, config.Model, config.ControlPlaneUrl);

await app.RunAsync();
