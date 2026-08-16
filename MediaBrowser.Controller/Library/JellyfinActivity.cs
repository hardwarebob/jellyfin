using System.Diagnostics;

namespace MediaBrowser.Controller.Library;

/// <summary>Shared ActivitySource for Jellyfin library-layer tracing.</summary>
public static class JellyfinActivity
{
    /// <summary>ActivitySource for library and repository operations.</summary>
    public static readonly ActivitySource Library = new("Jellyfin.Library");
}
