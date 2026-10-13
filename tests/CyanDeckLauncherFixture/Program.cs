using System.Text.Json;

// Disposable stand-in named DesktopDeck.exe for launcher integration tests.
var output = Environment.GetEnvironmentVariable("CYANDECK_LAUNCH_FIXTURE_OUTPUT");
if (string.IsNullOrWhiteSpace(output)) return 2;
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    Args = args,
    WorkingDirectory = Directory.GetCurrentDirectory(),
    ExecutableName = Path.GetFileName(Environment.ProcessPath)
}));
return 0;
