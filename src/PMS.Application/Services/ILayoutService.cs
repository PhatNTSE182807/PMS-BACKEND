using PMS.Application.Models.Layout;

namespace PMS.Application.Services;

/// <summary>
/// Layout 3D of a lot and its 2D fallback (SRS FR-3D-01/02/06/08). Layouts are generated from a
/// ready-made model when the Admin registers the lot (LotTemplates); nobody edits them by hand.
/// </summary>
public interface ILayoutService
{
    /// <summary>The ready-made parking buildings an Admin can pick.</summary>
    IReadOnlyList<LotTemplateResponseModel> GetTemplates();

    Task<LayoutResponseModel> GetPublishedAsync(int lotId);

    Task<LayoutStatusResponseModel> GetStatusAsync(int lotId);

    /// <summary>2D fallback; detail is "slots" (every cell) or "zones" (one block per zone).</summary>
    Task<Layout2DResponseModel> Get2DAsync(int lotId, string detail);
}
