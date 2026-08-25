using Microsoft.CodeAnalysis;

namespace Plank.SourceGen;

static class SyntaxExtensions
{
    public static SeparatedSyntaxList<T> ToSeparatedSyntaxList<T>(this IEnumerable<T> source) where T : SyntaxNode
    {
        var separated = new SeparatedSyntaxList<T>();
        foreach (var item in source)
            separated = separated.Add(item);
        return separated;
    }
}
