using System;
using System.Collections.Generic;
using System.Text;

namespace Plank.SourceGen;

/// <summary>Shared cursor plumbing for flat and nested generated pipeline writers.</summary>
static class RowCursorEmitter
{
    public static void AppendFactory(StringBuilder builder, string cursorName)
    {
        builder.AppendLine("        /// <summary>Creates a reusable row cursor. Call NextRow before assigning each row.</summary>");
        builder.Append("        public ").Append(cursorName).AppendLine(" CreateCursor()");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = GetSlotForRow();");
        builder.AppendLine("            return new(this);");
        builder.AppendLine("        }");
    }

    public static void AppendSlotMembers(StringBuilder builder)
    {
        builder.AppendLine("        internal int BufferVersion { get; private set; }");
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine("        internal int GetCursorIndex()");
        builder.AppendLine("        {");
        builder.AppendLine("            return Index;");
        builder.AppendLine("        }");
    }

    public static void AppendCursor(StringBuilder builder, string cursorName, string nextRowName, string refreshName,
        IReadOnlyList<string> columnTypes, Action appendProperties)
    {
        builder.AppendLine("    /// <summary>A reusable writable cursor over the pipeline's column buffers.</summary>");
        builder.AppendLine("    /// <remarks>Keep one mutable local outside the loop and pass it by ref to helpers. Call NextRow before each row, including after Reset or another writer/cursor advances. Assign only until the writer advances, completes, resets, or is disposed. The writer is not thread-safe. Column refs are rebound on the first NextRow and whenever buffers change.</remarks>");
        builder.Append("    public ref struct ").AppendLine(cursorName);
        builder.AppendLine("    {");
        builder.AppendLine("        readonly PipelineWriter _writer;");
        builder.AppendLine("        BufferSlot _ownerSlot;");
        builder.AppendLine("        int _bufferVersion;");
        builder.AppendLine("        int _index;");
        for (var i = 0; i < columnTypes.Count; i++)
            builder.Append("        ref ").Append(columnTypes[i]).Append(" _column").Append(i).AppendLine(";");
        builder.Append("        internal ").Append(cursorName).AppendLine("(PipelineWriter writer)");
        builder.AppendLine("        {");
        // A cursor that has not advanced must fault, not write before the beginning of an array.
        builder.AppendLine("            this = default;");
        builder.AppendLine("            _writer = writer;");
        builder.AppendLine("            _ownerSlot = null!;");
        for (var i = 0; i < columnTypes.Count; i++)
            builder.Append("            _column").Append(i)
                .Append(" = ref global::System.Runtime.CompilerServices.Unsafe.NullRef<")
                .Append(columnTypes[i]).AppendLine(">();");
        builder.AppendLine("        }");
        builder.AppendLine("        /// <summary>Commits the previous row and positions this cursor on the next writable row.</summary>");
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        builder.Append("        public void ").Append(nextRowName).AppendLine("()");
        builder.AppendLine("        {");
        builder.AppendLine("            var slot = _writer.GetSlotForNextRow();");
        builder.AppendLine("            var index = slot.GetCursorIndex();");
        builder.AppendLine("            if (!global::System.Object.ReferenceEquals(_ownerSlot, slot) || _bufferVersion != slot.BufferVersion)");
        builder.Append("                ").Append(refreshName).AppendLine("(slot);");
        builder.AppendLine("            _index = index;");
        builder.AppendLine("        }");
        // Keep O(columns) ref setup out of the loop body and of the inliner's budget.
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        builder.Append("        void ").Append(refreshName).AppendLine("(BufferSlot slot)");
        builder.AppendLine("        {");
        for (var i = 0; i < columnTypes.Count; i++)
            builder.Append("            _column").Append(i).Append(" = ref global::System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(slot._column")
                .Append(i).AppendLine(");");
        builder.AppendLine("            _ownerSlot = slot;");
        builder.AppendLine("            _bufferVersion = slot.BufferVersion;");
        builder.AppendLine("        }");
        appendProperties();
        builder.AppendLine("    }");
    }
}
