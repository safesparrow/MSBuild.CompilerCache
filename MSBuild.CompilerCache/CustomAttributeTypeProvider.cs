using System.Reflection.Metadata;

namespace MSBuild.CompilerCache;

internal sealed class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<Type>
{
    public Type GetPrimitiveType(PrimitiveTypeCode typeCode) =>
        typeCode switch
        {
            PrimitiveTypeCode.String => typeof(string),
            _ => throw new ArgumentOutOfRangeException(nameof(typeCode))
        };

    public Type GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => throw new NotImplementedException();
    public Type GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => throw new NotImplementedException();
    public Type GetSZArrayType(Type elementType) => throw new NotImplementedException();
    public Type GetSystemType() => throw new NotImplementedException();
    public Type GetTypeFromSerializedName(string name) => throw new NotImplementedException();
    public PrimitiveTypeCode GetUnderlyingEnumType(Type type) => throw new NotImplementedException();
    public bool IsSystemType(Type type) => throw new NotImplementedException();
}