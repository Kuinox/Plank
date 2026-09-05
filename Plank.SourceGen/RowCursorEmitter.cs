using System;
using System.Collections.Generic;
using System.Text;

namespace Plank.SourceGen;

/// <summary>Shared cursor plumbing for flat and nested generated pipeline writers.</summary>
static class RowCursorEmitter
{
    public static void AppendFactory(StringBuilder builder, string cursorName)
    {
        builder.AppendLine("        internal long CurrentBufferGeneration => BufferGeneration;");
        builder.AppendLine("        /// <summary>Creates a reusable row cursor. Call NextRow before assigning each row.</summary>");
        builder.Append("        public ").Append(cursorName).AppendLine(" CreateCursor()");
        builder.AppendLine("        {");
        builder.AppendLine("            _ = GetSlotForRow();");
        builder.AppendLine("            return new(this);");
        builder.AppendLine("        }");
    }

    public static void AppendSlotMembers(StringBuilder builder)
    {
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        builder.AppendLine("        internal int GetCursorIndex()");
        builder.AppendLine("        {");
        builder.AppendLine("            return Index;");
        builder.AppendLine("        }");
    }

    public static void AppendCursor(StringBuilder builder, string cursorName, string nextRowName, string refreshName,
        string buffersTypeName,
        IReadOnlyList<string> columnTypes, Action appendProperties)
    {
        builder.AppendLine("    /// <summary>A reusable writable cursor over the pipeline's column buffers.</summary>");
        builder.AppendLine("    /// <remarks>Keep one mutable local outside the loop and pass it by ref to helpers. Call NextRow before each row, including after Reset or another writer/cursor advances. Assign only until the writer advances, completes, resets, or is disposed. The writer is not thread-safe. Column refs are rebound on the first NextRow and whenever buffers change.</remarks>");
        builder.Append("    public ref struct ").AppendLine(cursorName);
        builder.AppendLine("    {");
        builder.AppendLine("        readonly PipelineWriter _writer;");
        builder.AppendLine("        int _index;");
        builder.Append("        ").Append(buffersTypeName).AppendLine(" _buffers;");
        builder.Append("        internal ").Append(cursorName).AppendLine("(PipelineWriter writer)");
        builder.AppendLine("        {");
        // A cursor that has not advanced must fault, not write before the beginning of an array.
        builder.AppendLine("            this = default;");
        builder.AppendLine("            _writer = writer;");
        builder.AppendLine("        }");
        builder.AppendLine("        /// <summary>Commits the previous row and positions this cursor on the next writable row.</summary>");
        builder.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
        builder.Append("        public void ").Append(nextRowName).AppendLine("()");
        builder.AppendLine("        {");
        builder.AppendLine("            var slot = _writer.GetSlotForNextRow();");
        builder.AppendLine("            var index = slot.GetCursorIndex();");
        builder.AppendLine("            var generation = _writer.CurrentBufferGeneration;");
        builder.AppendLine("            if (_buffers._bufferGeneration != generation)");
        builder.Append("                _buffers = ").Append(buffersTypeName).Append('.').Append(refreshName)
            .AppendLine("(slot, generation);");
        builder.AppendLine("            _index = index;");
        builder.AppendLine("        }");
        appendProperties();
        builder.Append("        ref struct ").AppendLine(buffersTypeName);
        builder.AppendLine("        {");
        builder.AppendLine("            internal BufferSlot _ownerSlot;");
        builder.AppendLine("            internal long _bufferGeneration;");
        for (var i = 0; i < columnTypes.Count; i++)
            builder.Append("            internal ref ").Append(columnTypes[i]).Append(" _column").Append(i).AppendLine(";");
        // Return fresh refs on the cold path; do not expose the cursor's address to a call.
        builder.AppendLine("            [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        builder.Append("            internal static ").Append(buffersTypeName).Append(' ').Append(refreshName)
            .AppendLine("(BufferSlot slot, long generation)");
        builder.AppendLine("            {");
        builder.Append("                ").Append(buffersTypeName).AppendLine(" buffers = default;");
        for (var i = 0; i < columnTypes.Count; i++)
            builder.Append("                buffers._column").Append(i).Append(" = ref global::System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(slot._column")
                .Append(i).AppendLine(");");
        builder.AppendLine("                buffers._ownerSlot = slot;");
        builder.AppendLine("                buffers._bufferGeneration = generation;");
        builder.AppendLine("                return buffers;");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }
}
