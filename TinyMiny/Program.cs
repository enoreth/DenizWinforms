using System.Text;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Builder.Metadata.Blob;
using AsmResolver.DotNet.Builder.Metadata.Strings;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.IO;
using AsmResolver.PE;
using AsmResolver.PE.DotNet.Builder;
using AsmResolver.PE.Code;
using AsmResolver.PE.DotNet;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using AsmResolver.PE.File;
using AsmResolver.PE.File.Headers;
using static AsmResolver.PE.DotNet.Cil.CilOpCodes;

namespace TinySharp;

internal static class Program
{
    public static void Main()
    {
        Enumerable
            .Range(0, 5)
            .Select(seq => $"output_{seq}.exe")
            .ToList()
            .ForEach(fileName => WriteExe(fileName));                   
    }

    public static void WriteExe(string fileName)
    {
        var module = new ModuleDefinition("Dummy");

        // Segment containing our string to print.
        var segment = new DataSegment(Encoding.ASCII.GetBytes("Hello, World!\0"));

        // Initialize a new PE image and set up some default values.
        var image = new PEImage
        {
            ImageBase = 0x00000000004e0000,    // Pick some random image base we will have our module be loaded at.
            PEKind = OptionalHeaderMagic.PE64, // Force PE64 to avoid import directory.
            MachineType = MachineType.Amd64    // PE64 needs 64-bit arch.
        };

        // Ensure PE is loaded at the provided image base.
        image.DllCharacteristics &= ~DllCharacteristics.DynamicBase;

        // Create new metadata streams.
        var tablesStream = new TablesStream();
        var blobStreamBuffer = new BlobStreamBuffer();
        var stringsStreamBuffer = new StringsStreamBuffer();

        // Add empty module row.
        tablesStream.GetTable<ModuleDefinitionRow>().Add(default);

        // Add container type def for our main function (<Module>).
        tablesStream.GetTable<TypeDefinitionRow>().Add(new TypeDefinitionRow(
            0, 0, 0, 0, 1, 1
        ));

        var methodTable = tablesStream.GetTable<MethodDefinitionRow>();

        // Add puts method.
        methodTable.Add(new MethodDefinitionRow(
            SegmentReference.Null,
            MethodImplAttributes.PreserveSig,
            MethodAttributes.Static | MethodAttributes.PInvokeImpl,
            stringsStreamBuffer.GetStringIndex("puts"),
            blobStreamBuffer.GetBlobIndex(new DummyProvider(),
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Void, module.CorLibTypeFactory.IntPtr), ThrowErrorListener.Instance),
            1
        ));

        // Add main method calling puts.
        using var codeStream = new MemoryStream();
        var assembler = new CilAssembler(new BinaryStreamWriter(codeStream), new CilOperandBuilder(new OriginalMetadataTokenProvider(null), ThrowErrorListener.Instance));
        assembler.WriteInstruction(new CilInstruction(Ldc_I4, 0x12345678)); // To be replaced with the address to the string to print (applied with a patch below).
        assembler.WriteInstruction(new CilInstruction(Call, new MetadataToken(TableIndex.Method, 1)));
        assembler.WriteInstruction(new CilInstruction(Ret));

        var body = new CilRawTinyMethodBody(codeStream.ToArray())
            .AsPatchedSegment()
            .Patch(2, AddressFixupType.Absolute32BitAddress, new Symbol(segment.ToReference())); // +0x1B0 is necessary due to a bug in AsmResolver 5.3.0. This won't be necessary in 5.4.0.

        methodTable.Add(new MethodDefinitionRow(
            body.ToReference(),
            0,
            MethodAttributes.Static,
            0,
            blobStreamBuffer.GetBlobIndex(new DummyProvider(),
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Void), ThrowErrorListener.Instance),
            1
        ));

        // Add urctbase module reference
        tablesStream.GetTable<ModuleReferenceRow>().Add(new ModuleReferenceRow(stringsStreamBuffer.GetStringIndex("ucrtbase")));

        // Add P/Invoke metadata to the puts method.
        tablesStream.GetTable<ImplementationMapRow>().Add(new ImplementationMapRow(
            ImplementationMapAttributes.CallConvCdecl,
            tablesStream.GetIndexEncoder(CodedIndex.MemberForwarded).EncodeToken(new MetadataToken(TableIndex.Method, 1)),
            stringsStreamBuffer.GetStringIndex("puts"),
            1
        ));

        // Define assembly manifest.
        tablesStream.GetTable<AssemblyDefinitionRow>().Add(new AssemblyDefinitionRow(
            0,
            1, 0, 0, 0,
            0,
            0,
            stringsStreamBuffer.GetStringIndex("puts"), // The CLR does not allow for assemblies with a null name. Reuse the name "puts" to safe space.
            0
        ));

        // Add all .NET metadata to the PE image.
        image.DotNetDirectory = new DotNetDirectory
        {
            EntryPoint = new MetadataToken(TableIndex.Method, 2),
            Metadata = new Metadata
            {
                VersionString = "v4.0.", // Needs the "." at the end. (original: v4.0.30319)
                Streams =
                {
                    tablesStream,
                    blobStreamBuffer.CreateStream(),
                    stringsStreamBuffer.CreateStream()
                }
            }
        };

        // Assemble PE file.
        var file = new MyBuilder().CreateFile(image);

        // Put string to print in the padding data.
        file.ExtraSectionData = segment;

        // Write to disk.
        file.Write(fileName);


    }
}




internal class DummyProvider : ITypeCodedIndexProvider
{
    public uint GetTypeDefOrRefIndex(ITypeDefOrRef type) => throw new NotImplementedException();
}

public class MyBuilder : ManagedPEFileBuilder
{
    protected override PESection CreateTextSection(IPEImage image, ManagedPEBuilderContext context)
    {
        // We override this method to only have it emit the bare minimum .text section.

        var methodTable = context.DotNetSegment.DotNetDirectory.Metadata?
            .GetStream<TablesStream>()!
            .GetTable<MethodDefinitionRow>()!;

        for (uint rid = 1; rid <= methodTable.Count; rid++)
        {
            ref var methodRow = ref methodTable.GetRowRef(rid);

            var bodySegment = methodRow.Body.IsBounded
                ? methodRow.Body.GetSegment()
                : null;

            if (bodySegment is not null)
            {
                context.DotNetSegment.MethodBodyTable.AddNativeBody(bodySegment, 4);
                methodRow.Body = bodySegment.ToReference();
            }
        }

        return new PESection(".text",
            SectionFlags.ContentCode | SectionFlags.MemoryExecute | SectionFlags.MemoryRead,
            context.DotNetSegment);
    }
}