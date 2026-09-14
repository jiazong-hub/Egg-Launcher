namespace Launcher.ChatGPT.Configuration;

public sealed class ChatGptConfigConflictException(string message) : InvalidOperationException(message);

