namespace TempusDemoArchive.Jobs;

internal static class JobPrompts
{
    public static string? ReadNonEmptyLine(string prompt, string missingMessage)
    {
        Console.WriteLine(prompt);
        var value = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine(missingMessage);
            return null;
        }

        return value;
    }

    public static string? ReadSteamIdentifier()
    {
        return ReadNonEmptyLine("Enter steam ID or Steam64:", "No identifier provided.");
    }

    public static string? ReadMapName()
    {
        return ReadNonEmptyLine("Input map name:", "No map name provided.");
    }

    public static string ReadClassDs()
    {
        Console.WriteLine("Input class 'D' or 'S':");
        var value = Console.ReadLine()!.ToUpperInvariant().Trim();
        if (value != "D" && value != "S")
        {
            throw new InvalidOperationException("Invalid class");
        }

        return value;
    }
}
