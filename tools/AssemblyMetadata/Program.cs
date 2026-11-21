using APIShared.AssemblyMetadata;

try { return MetadataCommand.Run(args); }
catch (Exception error)
{
    // Metadata values can contain arbitrary newlines; keep failure output bounded.
    string message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
    Console.Error.WriteLine(message.Length <= 512 ? message : "Metadata inspection failed; diagnostic exceeds 512 characters.");
    return 1;
}
