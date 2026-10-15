using CyanDeck.Services;

// All filesystem tests use disposable test directories, NEVER real user state.
var parent = Path.Combine(Path.GetTempPath(),
    "CyanDeck-full-reset-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(parent);
try
{
    var outside = Path.Combine(parent, "Desktop-original.txt");
    File.WriteAllText(outside, "untouched");
    var root = Path.Combine(parent, "CyanDeck");
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "layout.json"), "{\"groups\":[1]}");
    File.WriteAllText(Path.Combine(root, "calendar-events.json"), "[{\"title\":\"local\"}]");
    File.WriteAllText(Path.Combine(root, "calendar-google-token.dat"), "encrypted-token-mock");
    Directory.CreateDirectory(Path.Combine(root, "recovery"));
    File.WriteAllText(Path.Combine(root, "recovery", "snapshot.json"), "backup");

    var ticket = CompleteLocalResetService.CreateRequest(root);
    if (ticket.Length != 32 || !File.Exists(Path.Combine(root, CompleteLocalResetService.RequestFileName)))
        throw new Exception("Durable request not created.");
    try
    {
        CompleteLocalResetService.ExecutePending(root, new string('a', 32));
        throw new Exception("Invalid authorization was accepted.");
    }
    catch (InvalidDataException) { }
    if (!File.Exists(Path.Combine(root, "calendar-google-token.dat")))
        throw new Exception("Unverified request mutated user data.");

    CompleteLocalResetService.ExecutePending(root, ticket);
    if (Directory.Exists(root))
        throw new Exception("Confirmed local profile was not removed.");
    if (File.ReadAllText(outside) != "untouched")
        throw new Exception("Unrelated user file was changed.");

    Directory.CreateDirectory(root);
    var cancelled = CompleteLocalResetService.CreateRequest(root);
    CompleteLocalResetService.CancelRequest(root, cancelled);
    if (File.Exists(Path.Combine(root, CompleteLocalResetService.RequestFileName)))
        throw new Exception("Failed launch request was not canceled.");

    var stale = CompleteLocalResetService.CreateRequest(root);
    var requestPath = Path.Combine(root, CompleteLocalResetService.RequestFileName);
    File.WriteAllText(requestPath,
        "{\"Ticket\":\"" + stale + "\",\"IssuedUtc\":\"2020-01-01T00:00:00Z\"}");
    try
    {
        CompleteLocalResetService.ExecutePending(root, stale);
        throw new Exception("Expired request was accepted.");
    }
    catch (InvalidDataException) { }
    if (!Directory.Exists(root))
        throw new Exception("Expired request modified data.");

    try
    {
        CompleteLocalResetService.ExecutePending(parent, stale);
        throw new Exception("Non-CyanDeck directory was accepted.");
    }
    catch (InvalidDataException) { }
    Console.WriteLine("PASS: full local data reset token, expired request, external file and disposable data");
}
finally
{
    if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
}
