using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

const string repoUrl = "https://github.com/tinocoh/azure-finops-agent.git";
const string appUserSecretsId = "azure-finops-agent-dashboard";
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var dashboardRoot = Path.Combine(repoRoot, "agent", "src", "Dashboard");
var webuiRoot = Path.Combine(dashboardRoot, "webui");
var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzureFinOpsAgentDemoLauncher");
var stateFile = Path.Combine(stateDir, "demo-resources.json");

Console.Title = "Azure FinOps Agent Local Demo Launcher";
Directory.CreateDirectory(stateDir);

Header("Azure FinOps Agent Local Demo Launcher");
Line("This launcher is for local demo/pilot use. It does not deploy the full enterprise app.");
Line("Secrets are stored only in .NET User Secrets on this machine, not in Key Vault and not in the repo.");
Console.WriteLine();

try
{
    Require("git", "--version", "Git is required: https://git-scm.com/downloads");
    Require("dotnet", "--version", ".NET 10 SDK is required: https://dotnet.microsoft.com/download");
    Require("npm", "--version", "Node.js/npm is required: https://nodejs.org/");

    while (true)
    {
        Header("Choose an option");
        Line("1. Provision minimal Azure OpenAI resources, configure local secrets, and run the app");
        Line("2. Use an existing Azure OpenAI endpoint/deployment and run the app");
        Line("3. Run the app with current local configuration");
        Line("4. Cleanup demo Azure resources and delete local secrets");
        Line("5. Exit");
        Console.Write("> ");
        var choice = Console.ReadLine()?.Trim();
        Console.WriteLine();

        switch (choice)
        {
            case "1":
                ProvisionAzureOpenAiAndRun();
                return;
            case "2":
                ConfigureExistingAzureOpenAi();
                BuildAndRun();
                return;
            case "3":
                BuildAndRun();
                return;
            case "4":
                Cleanup();
                return;
            case "5":
                return;
            default:
                Warn("Choose 1, 2, 3, 4, or 5.");
                break;
        }
    }
}
catch (Exception ex)
{
    Error(ex.Message);
    Console.WriteLine();
    Line("Press any key to close.");
    Console.ReadKey(intercept: true);
}

void ProvisionAzureOpenAiAndRun()
{
    Require("az", "--version", "Azure CLI is required for provisioning: https://learn.microsoft.com/cli/azure/install-azure-cli");

    Header("Consent required");
    Warn("This will sign you into Azure and create resources in your Azure subscription.");
    Warn("The resources can generate Azure charges until you delete them.");
    Warn("The launcher will create a resource group, an Azure OpenAI account, and a model deployment.");
    Warn("The launcher will store only the resulting endpoint/deployment in local .NET User Secrets.");
    Warn("No client secrets are stored in the repository. Key Vault is not used because this is a local-only demo.");
    Warn("Cleanup deletes the demo resource group and removes the app's local .NET User Secrets from this machine.");
    Console.WriteLine();
    Console.Write("Type I UNDERSTAND to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "I UNDERSTAND", StringComparison.Ordinal))
    {
        Warn("Cancelled.");
        return;
    }

    EnsureRepo();
    EnsureAzureLogin();

    var subscriptions = Azure("account list --query \"[].{name:name,id:id,tenantId:tenantId,isDefault:isDefault}\" -o json");
    var parsed = JsonSerializer.Deserialize<List<Subscription>>(subscriptions) ?? [];
    if (parsed.Count == 0) throw new InvalidOperationException("No Azure subscriptions were found for this account.");

    Header("Select subscription");
    for (var i = 0; i < parsed.Count; i++)
    {
        var marker = parsed[i].isDefault ? " (default)" : "";
        Line($"{i + 1}. {parsed[i].name} [{parsed[i].id}]{marker}");
    }
    Console.Write("> ");
    if (!int.TryParse(Console.ReadLine(), out var selected) || selected < 1 || selected > parsed.Count)
        throw new InvalidOperationException("Invalid subscription selection.");

    var sub = parsed[selected - 1];
    Run("az", $"account set --subscription {Quote(sub.id)}", repoRoot);

    Console.Write("Azure region [eastus]: ");
    var region = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(region)) region = "eastus";

    Console.Write("Model deployment name [gpt-4.1-mini]: ");
    var deployment = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(deployment)) deployment = "gpt-4.1-mini";

    var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    var rg = $"rg-azure-finops-agent-demo-{suffix}";
    var account = $"finopsdemo{suffix[^8..]}";

    Header("Provisioning Azure resources");
    Run("az", $"group create --name {Quote(rg)} --location {Quote(region)} --only-show-errors", repoRoot);
    Run("az", $"cognitiveservices account create --name {Quote(account)} --resource-group {Quote(rg)} --location {Quote(region)} --kind OpenAI --sku S0 --custom-domain {Quote(account)} --yes --only-show-errors", repoRoot);

    var endpoint = Azure($"cognitiveservices account show --name {Quote(account)} --resource-group {Quote(rg)} --query properties.endpoint -o tsv").Trim();
    if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Azure OpenAI endpoint was not returned by Azure CLI.");

    TryCreateDeployment(rg, account, deployment);
    ConfigureUserSecrets(endpoint, deployment);
    SaveState(new DemoState(sub.id, sub.tenantId, rg, account, region, endpoint, deployment));

    BuildAndRun();
}

void TryCreateDeployment(string resourceGroup, string accountName, string deployment)
{
    Header("Creating model deployment");
    var attempts = new[]
    {
        $"cognitiveservices account deployment create --resource-group {Quote(resourceGroup)} --name {Quote(accountName)} --deployment-name {Quote(deployment)} --model-name {Quote(deployment)} --model-format OpenAI --sku-capacity 1 --sku-name GlobalStandard --only-show-errors",
        $"cognitiveservices account deployment create --resource-group {Quote(resourceGroup)} --name {Quote(accountName)} --deployment-name {Quote(deployment)} --model-name {Quote(deployment)} --model-format OpenAI --scale-settings-scale-type Standard --scale-settings-capacity 1 --only-show-errors"
    };

    foreach (var attempt in attempts)
    {
        try
        {
            Run("az", attempt, repoRoot);
            return;
        }
        catch (Exception ex)
        {
            Warn($"Deployment command failed: {ex.Message}");
        }
    }

    Warn("Could not create the model deployment automatically.");
    Warn("The Azure OpenAI account was created, but you may need to create a deployment manually in Azure AI Foundry.");
    Warn($"After creating it, set the deployment name to '{deployment}' or rerun option 2 with the deployment you created.");
}

void ConfigureExistingAzureOpenAi()
{
    EnsureRepo();
    Header("Configure existing Azure OpenAI");
    Line("These values are stored in .NET User Secrets on this machine only.");
    Line("They are not stored in the repository and are removed by launcher cleanup.");
    Console.Write("Azure OpenAI endpoint: ");
    var endpoint = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Azure OpenAI endpoint is required.");

    Console.Write("Deployment name [gpt-5.4]: ");
    var deployment = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(deployment)) deployment = "gpt-5.4";

    ConfigureUserSecrets(endpoint, deployment);
}

void BuildAndRun()
{
    EnsureRepo();
    if (!HasConfiguredAzureOpenAi())
    {
        throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured. Choose option 1 to provision Azure OpenAI or option 2 to use an existing endpoint.");
    }

    Run("npm", "ci --no-audit --no-fund", webuiRoot);
    Run("npm", "run build", webuiRoot);
    Run("dotnet", "build -c Release", dashboardRoot);

    if (IsPortListening(5000))
    {
        throw new InvalidOperationException("Port 5000 is already in use. Stop the existing local app and run the launcher again.");
    }

    Header("Starting local app");
    Line("Opening http://localhost:5000/");
    var psi = new ProcessStartInfo("dotnet", "run -c Release --no-build --urls http://localhost:5000")
    {
        WorkingDirectory = dashboardRoot,
        UseShellExecute = false,
    };
    psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    psi.Environment["ASPNETCORE_URLS"] = "http://localhost:5000";
    var app = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the Dashboard process.");
    Thread.Sleep(3000);
    Process.Start(new ProcessStartInfo("http://localhost:5000/") { UseShellExecute = true });
    Line("Keep this window open. Press Ctrl+C to stop the app.");
    app.WaitForExit();
}

void Cleanup()
{
    Header("Cleanup");
    Warn("Cleanup removes the demo resource group recorded by this launcher and deletes local .NET User Secrets for this app.");
    Warn("If you used option 2 with an existing Azure OpenAI resource, cleanup deletes only local secrets, not that existing resource.");
    Console.Write("Type DELETE to continue: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "DELETE", StringComparison.Ordinal))
    {
        Warn("Cancelled.");
        return;
    }

    if (File.Exists(stateFile))
    {
        var state = JsonSerializer.Deserialize<DemoState>(File.ReadAllText(stateFile));
        if (state is not null && !string.IsNullOrWhiteSpace(state.ResourceGroupName))
        {
            EnsureAzureLogin();
            Run("az", $"group delete --name {Quote(state.ResourceGroupName)} --yes --no-wait --only-show-errors", repoRoot);
            Line($"Resource group deletion requested: {state.ResourceGroupName}");
        }
        File.Delete(stateFile);
    }
    else
    {
        Warn("No launcher-created demo resource group was recorded.");
    }

    DeleteUserSecrets();
    Line("Cleanup complete. Local app secrets were deleted from .NET User Secrets.");
}

void EnsureRepo()
{
    Directory.CreateDirectory(@"C:\dev");
    if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
    {
        Run("git", $"clone {repoUrl} {Quote(repoRoot)}", @"C:\dev");
    }
    else
    {
        Run("git", "fetch origin main", repoRoot);
    }

    Run("git", "checkout main", repoRoot);
    Run("git", "pull --ff-only origin main", repoRoot);
}

void EnsureAzureLogin()
{
    try
    {
        Azure("account show -o none");
    }
    catch
    {
        Run("az", "login", repoRoot);
    }
}

bool HasConfiguredAzureOpenAi()
{
    var secrets = RunCapture("dotnet", "user-secrets list", dashboardRoot);
    return secrets.Contains("AzureOpenAI:Endpoint", StringComparison.OrdinalIgnoreCase);
}

void ConfigureUserSecrets(string endpoint, string deployment)
{
    Run("dotnet", $"user-secrets set \"AzureOpenAI:Endpoint\" {Quote(endpoint)}", dashboardRoot);
    Run("dotnet", $"user-secrets set \"AzureOpenAI:DeploymentName\" {Quote(deployment)}", dashboardRoot);
    Line("Local .NET User Secrets configured.");
}

void DeleteUserSecrets()
{
    var secretsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", appUserSecretsId);
    if (Directory.Exists(secretsRoot))
    {
        Directory.Delete(secretsRoot, recursive: true);
    }
}

void SaveState(DemoState state)
{
    File.WriteAllText(stateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
}

string Azure(string arguments) => RunCapture("az", arguments, repoRoot);

static void Require(string command, string args, string help)
{
    try { Run(command, args, Directory.GetCurrentDirectory(), quiet: true); }
    catch { throw new InvalidOperationException(help); }
}

static string RunCapture(string fileName, string arguments, string workingDirectory)
{
    var (actualFileName, actualArguments) = NormalizeCommand(fileName, arguments);
    var psi = new ProcessStartInfo(actualFileName, actualArguments)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"{fileName} failed." : stderr.Trim());
    return stdout;
}

static void Run(string fileName, string arguments, string workingDirectory, bool quiet = false)
{
    if (!quiet) Line($"> {fileName} {arguments}");
    var (actualFileName, actualArguments) = NormalizeCommand(fileName, arguments);
    var psi = new ProcessStartInfo(actualFileName, actualArguments)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
    p.OutputDataReceived += (_, e) => { if (!quiet && e.Data is not null) Console.WriteLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (!quiet && e.Data is not null) Console.Error.WriteLine(e.Data); };
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new InvalidOperationException($"{fileName} {arguments} failed with exit code {p.ExitCode}.");
}

static (string FileName, string Arguments) NormalizeCommand(string fileName, string arguments)
{
    return OperatingSystem.IsWindows() && fileName.Equals("npm", StringComparison.OrdinalIgnoreCase)
        ? ("cmd.exe", $"/c npm {arguments}")
        : (fileName, arguments);
}

static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

static bool IsPortListening(int port) =>
    IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port);

static void Header(string text)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine();
    Console.WriteLine(text);
    Console.WriteLine(new string('-', text.Length));
    Console.ResetColor();
}

static void Line(string text)
{
    Console.WriteLine(text);
}

static void Warn(string text)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(text);
    Console.ResetColor();
}

static void Error(string text)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(text);
    Console.ResetColor();
}

record Subscription(string name, string id, string tenantId, bool isDefault);
record DemoState(string SubscriptionId, string TenantId, string ResourceGroupName, string AccountName, string Region, string Endpoint, string DeploymentName);
