namespace MyLog.Models;

public record RemoteProjectDto(string ProjectName);
public record RemoteLogRequestDto(string ProjectName, string? FileName = null);
