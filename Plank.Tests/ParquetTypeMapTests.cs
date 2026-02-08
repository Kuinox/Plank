using Plank.Schema;

#pragma warning disable CA2007
namespace Plank.Tests;

internal sealed class ParquetTypeMapTests
{
    [Test]
    public async Task GetPhysicalTypeGenericCoversSupportedAndUnsupportedTypes()
    {
        var mapType = typeof(ParquetSchema).Assembly.GetType("Plank.Schema.ParquetTypeMap", throwOnError: true)!;
        var getPhysicalType = mapType.GetMethod("GetPhysicalType", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

        await Assert.That(InvokeGeneric(getPhysicalType, typeof(bool))).IsEqualTo(ParquetPhysicalType.Boolean);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(int))).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(DateOnly))).IsEqualTo(ParquetPhysicalType.Int32);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(long))).IsEqualTo(ParquetPhysicalType.Int64);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(DateTime))).IsEqualTo(ParquetPhysicalType.Int64);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(DateTimeOffset))).IsEqualTo(ParquetPhysicalType.Int64);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(TimeOnly))).IsEqualTo(ParquetPhysicalType.Int64);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(float))).IsEqualTo(ParquetPhysicalType.Float);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(double))).IsEqualTo(ParquetPhysicalType.Double);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(byte[]))).IsEqualTo(ParquetPhysicalType.ByteArray);
        await Assert.That(InvokeGeneric(getPhysicalType, typeof(string))).IsEqualTo(ParquetPhysicalType.ByteArray);

        var unsupportedThrows = false;
        try
        {
            _ = InvokeGeneric(getPhysicalType, typeof(Guid));
        }
        catch (System.Reflection.TargetInvocationException ex) when (
            ex.InnerException is NotSupportedException ||
            ex.InnerException is TypeInitializationException { InnerException: NotSupportedException })
        {
            unsupportedThrows = true;
        }

        await Assert.That(unsupportedThrows).IsTrue();
    }

    static ParquetPhysicalType InvokeGeneric(System.Reflection.MethodInfo method, Type type)
        => (ParquetPhysicalType)method.MakeGenericMethod(type).Invoke(null, null)!;
}
#pragma warning restore CA2007
