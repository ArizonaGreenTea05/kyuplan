namespace Kyuplan.Client.Api;

/// <summary>A downloaded backup: the file name the API suggested and the file.</summary>
public sealed record BackupFile(string FileName, byte[] Content);
