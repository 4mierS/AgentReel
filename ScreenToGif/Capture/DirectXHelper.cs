using System.Collections.Generic;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;

namespace ScreenToGif.Capture;

/// <summary>
/// Helpers that keep the Desktop Duplication captures close to their SharpDX shape after the move to Vortice.
/// </summary>
internal static class DirectXHelper
{
    /// <summary>
    /// Copies a region of the source into the destination, with the arguments in the order SharpDX used (source first).
    /// Vortice follows the native order (destination first), so the call sites keep their old order through this.
    /// </summary>
    public static void CopyRegion(this ID3D11DeviceContext context, ID3D11Resource source, int sourceSubresource, Box? sourceRegion, ID3D11Resource destination, int destinationSubresource, int destinationX = 0, int destinationY = 0, int destinationZ = 0)
    {
        context.CopySubresourceRegion(destination, (uint)destinationSubresource, (uint)destinationX, (uint)destinationY, (uint)destinationZ, source, (uint)sourceSubresource, sourceRegion);
    }

    /// <summary>
    /// All outputs (screens) of all adapters.
    /// </summary>
    public static List<IDXGIOutput> GetOutputs(this IDXGIFactory1 factory)
    {
        var outputs = new List<IDXGIOutput>();

        for (var adapterIndex = 0u; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            using (adapter)
            {
                for (var outputIndex = 0u; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    outputs.Add(output);
            }
        }

        return outputs;
    }
}
