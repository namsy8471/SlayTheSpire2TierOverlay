using System.Security.Cryptography;
using System.Text;
using Sts2Overlay.App.Interop;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The IIDs of generic WinRT interfaces (here the two TypedEventHandler instantiations WgcCapturer implements) are not
/// written anywhere as attributes: they are computed from a type signature. These tests implement that rule
/// independently, prove it on IIDs the Windows SDK publishes, then check the constants in WgcInterop against it.
/// </summary>
public class WinRTIidTests
{
    /// <summary>
    /// WinRT type system rule for parameterized interfaces: UUID version 5 (SHA-1) of the UTF-8 signature string, in the
    /// namespace 11f47ad5-7b73-42c0-abae-878b1e16adee. The namespace bytes are hashed big-endian (RFC 4122 byte order),
    /// and the first 16 bytes of the hash, version/variant bits set, are read back big-endian.
    /// </summary>
    private static Guid ParameterizedIid(string signature)
    {
        var ns = new Guid("11f47ad5-7b73-42c0-abae-878b1e16adee").ToByteArray(bigEndian: true);
        var data = ns.Concat(Encoding.UTF8.GetBytes(signature)).ToArray();
        var hash = SHA1.HashData(data);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }

    // Generic interface ids: cppwinrt\winrt\impl\windows.foundation.collections.0.h lines 79, 83, 89 and
    // cppwinrt\winrt\impl\windows.foundation.0.h line 237 (Windows SDK 10.0.22621.0).
    private const string IIterable = "{faa585ea-6214-4217-afda-7f46de5869b3}";
    private const string IKeyValuePair = "{02b51929-c1c4-4a7e-8940-0312b5c18500}";
    private const string IMap = "{3c2925fe-8519-45c1-aa79-197b6718c1c1}";
    private const string TypedEventHandler = "{9de1c534-6ae1-11e0-84e1-18a905bcc53f}";

    /// <summary>Instantiations whose IIDs the SDK header winrt\windows.foundation.h (10.0.22621.0) prints, line noted.</summary>
    [Theory]
    // IKeyValuePair<HSTRING, IInspectable*>, line 409.
    [InlineData("pinterface(" + IKeyValuePair + ";string;cinterface(IInspectable))", "09335560-6c6b-5a26-9348-97b781132b20")]
    // IMap<HSTRING, HSTRING>, line 713.
    [InlineData("pinterface(" + IMap + ";string;string)", "f6d1f700-49c2-52ae-8154-826f9908773c")]
    // IIterable<IKeyValuePair<HSTRING, HSTRING>>, line 534: a parameterized interface nested in another.
    [InlineData("pinterface(" + IIterable + ";pinterface(" + IKeyValuePair + ";string;string))", "e9bdaaf0-cbf6-5c72-be90-29cbf3a1319b")]
    // TypedEventHandler<IMemoryBufferReference*, IInspectable*>, line 866 (IMemoryBufferReference = fbc4dd29-..., line 1539).
    [InlineData("pinterface(" + TypedEventHandler + ";{fbc4dd29-245b-11e4-af98-689423260cf8};cinterface(IInspectable))", "f4637d4a-0760-5431-bfc0-24eb1d4f6c4f")]
    public void Algorithm_reproduces_published_iids(string signature, string expected)
        => Assert.Equal(new Guid(expected), ParameterizedIid(signature));

    /// <summary>
    /// A runtime class argument is written rc(name;{default interface}); these are the exact signatures C#/WinRT
    /// (GuidGenerator.GetSignature) produced for the two handlers while the app still used the projection.
    /// </summary>
    [Fact]
    public void FrameArrived_handler_iid_matches_its_signature()
        => Assert.Equal(
            ParameterizedIid("pinterface(" + TypedEventHandler + ";rc(Windows.Graphics.Capture.Direct3D11CaptureFramePool;{24eb6d22-1975-422e-82e7-780dbd8ddf24});cinterface(IInspectable))"),
            WgcInterop.IID_FrameArrivedHandler);

    [Fact]
    public void Closed_handler_iid_matches_its_signature()
        => Assert.Equal(
            ParameterizedIid("pinterface(" + TypedEventHandler + ";rc(Windows.Graphics.Capture.GraphicsCaptureItem;{79c3f95b-31f7-4ec2-a464-632ef5d30760});cinterface(IInspectable))"),
            WgcInterop.IID_ItemClosedHandler);
}
