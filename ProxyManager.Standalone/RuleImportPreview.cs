using System.IO;
using ProxyManager.Standalone.Localization;

namespace ProxyManager.Standalone;

public enum RuleImportDisposition
{
    Add,
    SkipExisting,
    SkipDuplicateInFile
}

public sealed class RuleImportPreviewRow
{
    public int Index { get; init; }
    public string ExeName { get; init; } = "";
    public string ModeText { get; init; } = "";
    public string ConditionSummary { get; init; } = "";
    public string DispositionText { get; init; } = "";
    public RuleImportDisposition Disposition { get; init; }
}

public sealed class RuleImportPreview
{
    public IReadOnlyList<RuleImportPreviewRow> Rows { get; init; } = [];
    public IReadOnlyList<ProxyRule> RulesToAdd { get; init; } = [];
    public int AddCount { get; init; }
    public int SkipExistingCount { get; init; }
    public int SkipDuplicateInFileCount { get; init; }

    public int SkipCount => SkipExistingCount + SkipDuplicateInFileCount;
    public bool HasAdditions => AddCount > 0;

    public string SummaryText => string.Format(
        Strings.ImportPreviewSummaryFormat,
        AddCount,
        SkipExistingCount,
        SkipDuplicateInFileCount);
}

/// <summary>
/// 规则导入分类：完整身份键重复则跳过，不覆盖现有规则。
/// 预览与提交共用，避免对话框与写入分叉。
/// </summary>
public static class RuleImportPlanner
{
    public static RuleImportPreview Build(
        IReadOnlyList<ProxyRule> existing,
        IReadOnlyList<ProxyRule> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        var existingKeys = new HashSet<string>(
            existing.Select(RuleIdentity.CreateKey),
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RuleImportPreviewRow>(incoming.Count);
        var toAdd = new List<ProxyRule>();
        var skipExisting = 0;
        var skipDuplicateInFile = 0;

        for (var index = 0; index < incoming.Count; index++)
        {
            var rule = incoming[index];
            if (rule == null)
                throw new InvalidDataException(Strings.ErrImportEmptyRules);

            var key = RuleIdentity.CreateKey(rule);
            RuleImportDisposition disposition;
            if (existingKeys.Contains(key))
            {
                disposition = RuleImportDisposition.SkipExisting;
                skipExisting++;
            }
            else if (!seen.Add(key))
            {
                disposition = RuleImportDisposition.SkipDuplicateInFile;
                skipDuplicateInFile++;
            }
            else
            {
                disposition = RuleImportDisposition.Add;
                toAdd.Add(rule);
            }

            rows.Add(new RuleImportPreviewRow
            {
                Index = index + 1,
                ExeName = rule.ExeName ?? "",
                ModeText = rule.ModeText,
                ConditionSummary = rule.ConditionSummary,
                Disposition = disposition,
                DispositionText = DispositionText(disposition)
            });
        }

        return new RuleImportPreview
        {
            Rows = rows,
            RulesToAdd = toAdd,
            AddCount = toAdd.Count,
            SkipExistingCount = skipExisting,
            SkipDuplicateInFileCount = skipDuplicateInFile
        };
    }

    private static string DispositionText(RuleImportDisposition disposition) => disposition switch
    {
        RuleImportDisposition.Add => Strings.ImportPreviewDispositionAdd,
        RuleImportDisposition.SkipExisting => Strings.ImportPreviewDispositionSkipExisting,
        RuleImportDisposition.SkipDuplicateInFile => Strings.ImportPreviewDispositionSkipDuplicateInFile,
        _ => disposition.ToString()
    };
}
