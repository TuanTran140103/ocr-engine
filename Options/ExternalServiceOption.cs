namespace OCREngine.Options;

public class ExternalServiceOption
{
    public DocOriOption DocOri { get; set; } = new();
}

public class DocOriOption
{
    public string BaseUrl { get; set; } = string.Empty;
    public int BatchSize { get; set; } = 5;
}
