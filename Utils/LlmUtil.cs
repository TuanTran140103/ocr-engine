using OCREngine.Models.Enum;
using OCREngine.Prompts;

namespace OCREngine.Utils;

public static class LlmUtil
{
    public static readonly List<string> supportedModels = Enum.GetNames<LlmSupport>().ToList();

    public static bool IsSupported(string modelId)
    {
        return GetModelEnum(modelId) != null;
    }

    public static LlmSupport? GetModelEnum(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;

        foreach (LlmSupport e in Enum.GetValues(typeof(LlmSupport)))
        {
            if (e.ToString().Equals(modelId, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }
        }
        return null;
    }
}
