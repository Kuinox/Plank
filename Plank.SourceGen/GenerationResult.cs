using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Plank.SourceGen;

sealed class GenerationResult : IEquatable<GenerationResult>
{
    readonly string? _hintName;
    readonly string? _source;
    readonly ImmutableArray<GenerationDiagnostic> _diagnostics;

    GenerationResult(string? hintName, string? source, ImmutableArray<GenerationDiagnostic> diagnostics)
    {
        _hintName = hintName;
        _source = source;
        _diagnostics = diagnostics;
    }

    internal static GenerationResult Source(string hintName, string source)
        => new(hintName, source, []);

    internal static GenerationResult Diagnostic(DiagnosticDescriptor descriptor, Location? location,
        params string[] arguments)
        => Diagnostics([GenerationDiagnostic.Create(descriptor, location, arguments)]);

    internal static GenerationResult Diagnostics(IEnumerable<GenerationDiagnostic> diagnostics)
        => new(null, null, diagnostics.ToImmutableArray());

    internal static GenerationResult Diagnostics(ImmutableArray<GenerationDiagnostic> diagnostics)
        => new(null, null, diagnostics);

    internal void Emit(SourceProductionContext context)
    {
        if (_source is not null)
            context.AddSource(_hintName!, _source);
        foreach (var diagnostic in _diagnostics)
            context.ReportDiagnostic(diagnostic.ToDiagnostic());
    }

    public bool Equals(GenerationResult? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || !string.Equals(_hintName, other._hintName, StringComparison.Ordinal) ||
            !string.Equals(_source, other._source, StringComparison.Ordinal) ||
            _diagnostics.Length != other._diagnostics.Length)
            return false;
        for (var i = 0; i < _diagnostics.Length; i++)
            if (!_diagnostics[i].Equals(other._diagnostics[i]))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as GenerationResult);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + (_hintName?.GetHashCode() ?? 0);
            hash = hash * 31 + (_source?.GetHashCode() ?? 0);
            foreach (var diagnostic in _diagnostics)
                hash = hash * 31 + diagnostic.GetHashCode();
            return hash;
        }
    }
}

sealed class GenerationDiagnostic : IEquatable<GenerationDiagnostic>
{
    readonly DiagnosticDescriptor _descriptor;
    readonly ImmutableArray<string> _arguments;
    readonly bool _hasSourceLocation;
    readonly string _path;
    readonly TextSpan _sourceSpan;
    readonly LinePositionSpan _lineSpan;

    GenerationDiagnostic(DiagnosticDescriptor descriptor, ImmutableArray<string> arguments,
        bool hasSourceLocation, string path, TextSpan sourceSpan, LinePositionSpan lineSpan)
    {
        _descriptor = descriptor;
        _arguments = arguments;
        _hasSourceLocation = hasSourceLocation;
        _path = path;
        _sourceSpan = sourceSpan;
        _lineSpan = lineSpan;
    }

    internal static GenerationDiagnostic Create(DiagnosticDescriptor descriptor, Location? location,
        params string[] arguments)
    {
        if (location is not { IsInSource: true })
            return new GenerationDiagnostic(descriptor, arguments.ToImmutableArray(), false,
                string.Empty, default, default);

        var lineSpan = location.GetLineSpan();
        return new GenerationDiagnostic(descriptor, arguments.ToImmutableArray(), true,
            lineSpan.Path, location.SourceSpan, lineSpan.Span);
    }

    internal Diagnostic ToDiagnostic()
    {
        var location = _hasSourceLocation
            ? Location.Create(_path, _sourceSpan, _lineSpan)
            : Location.None;
        var arguments = new object[_arguments.Length];
        for (var i = 0; i < _arguments.Length; i++)
            arguments[i] = _arguments[i];
        return Diagnostic.Create(_descriptor, location, arguments);
    }

    public bool Equals(GenerationDiagnostic? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || !ReferenceEquals(_descriptor, other._descriptor) ||
            _hasSourceLocation != other._hasSourceLocation ||
            !string.Equals(_path, other._path, StringComparison.Ordinal) ||
            !_sourceSpan.Equals(other._sourceSpan) || !_lineSpan.Equals(other._lineSpan) ||
            _arguments.Length != other._arguments.Length)
            return false;
        for (var i = 0; i < _arguments.Length; i++)
            if (!string.Equals(_arguments[i], other._arguments[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as GenerationDiagnostic);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = _descriptor.GetHashCode();
            hash = hash * 31 + _hasSourceLocation.GetHashCode();
            hash = hash * 31 + _path.GetHashCode();
            hash = hash * 31 + _sourceSpan.GetHashCode();
            hash = hash * 31 + _lineSpan.GetHashCode();
            foreach (var argument in _arguments)
                hash = hash * 31 + argument.GetHashCode();
            return hash;
        }
    }
}
