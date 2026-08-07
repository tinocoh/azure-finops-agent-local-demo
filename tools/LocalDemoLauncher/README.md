# Azure FinOps Agent local demo launcher

This launcher is a local-first demo/pilot experience for Windows users.

It is intentionally separate from the committee review repo so the OSS review path can remain stable while the demo onboarding experience evolves.

## Goals

- Let a new user run the correct Azure FinOps Agent Dashboard locally.
- Avoid asking the user to manually discover Azure OpenAI endpoints before seeing the product.
- Make Azure cost and permission implications explicit before provisioning anything.
- Keep all secrets local to the user's machine.
- Provide a cleanup path that removes both Azure demo resources and local secrets created by this launcher.

## Modes

Before showing the menu, the launcher runs a prerequisite check for:

- Git
- .NET 10 SDK
- Node.js/npm
- Azure CLI availability for provisioning/cleanup

If a required tool is missing on Windows and `winget` is available, the launcher offers to install it. After installation, the user may need to close the launcher and open a new terminal so the updated PATH is visible.

1. **Provision minimal Azure OpenAI resources**
   - Runs `az login` if needed.
   - Lets the user choose a subscription.
   - Creates a demo resource group.
   - Creates an Azure OpenAI resource.
   - Attempts to create a model deployment.
   - Stores the endpoint and deployment name in .NET User Secrets.
   - Builds and launches the local app.

2. **Use existing Azure OpenAI**
   - Prompts for endpoint and deployment name.
   - Stores values in .NET User Secrets.
   - Builds and launches the local app.

3. **Run current local configuration**
   - Uses existing .NET User Secrets.
   - Builds and launches the local app.

4. **Cleanup**
   - Deletes the resource group created by the launcher, if any.
   - Deletes the launcher's recorded state file.
   - Deletes the app's .NET User Secrets folder.

## Consent and cost warning

Before provisioning, the launcher requires the user to type:

```text
I UNDERSTAND
```

The warning explicitly states that:

- Azure sign-in is required.
- Resources will be created in the user's Azure subscription.
- Those resources can generate Azure charges.
- The user must have permission to create resources.
- Cleanup should be used to stop future demo costs.

## Secret handling

This is a local demo, so Key Vault is intentionally not used.

The launcher stores only local app configuration in:

```text
%APPDATA%\Microsoft\UserSecrets\azure-finops-agent-dashboard
```

Stored values are:

- `AzureOpenAI:Endpoint`
- `AzureOpenAI:DeploymentName`

These values are:

- not written to the repository,
- not committed to git,
- not uploaded by the launcher,
- removed by launcher cleanup.

## Cleanup guarantee

When cleanup is confirmed, the launcher:

1. Requests deletion of the recorded demo Azure resource group using `az group delete --no-wait`.
2. Deletes its local state file from `%APPDATA%\AzureFinOpsAgentDemoLauncher`.
3. Deletes `%APPDATA%\Microsoft\UserSecrets\azure-finops-agent-dashboard`.

If the user configured an existing Azure OpenAI endpoint manually, cleanup deletes local secrets but does not delete that existing Azure resource.

## Troubleshooting

### Azure CLI appears installed but the launcher says it is missing

On Windows, Azure CLI is commonly exposed as `az.cmd`. A normal Command Prompt can run `az login`, while a launcher that invokes `az` incorrectly may fail to detect it. The launcher now resolves `az` through `cmd.exe` to match the user's shell behavior.

If detection still fails:

1. Close the launcher.
2. Open a new Command Prompt or PowerShell window.
3. Run:

   ```powershell
   az --version
   ```

4. If that works, run the launcher again.

If `az --version` does not work, install Azure CLI:

```powershell
winget install --id Microsoft.AzureCLI --exact --source winget
```
