#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Querying;

namespace Emby.Server.Implementations.Playlists
{
    [RequiresSourceSerialisation]
    public class PlaylistsFolder : BasePluginFolder
    {
        public PlaylistsFolder()
        {
            Name = "Playlists";
        }

        [JsonIgnore]
        public override bool IsHidden => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override CollectionType? CollectionType => Jellyfin.Data.Enums.CollectionType.playlists;

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user)
        {
            return base.GetEligibleChildrenForRecursiveChildren(user).OfType<Playlist>();
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
        {
            using var activity = JellyfinActivity.Library.StartActivity("PlaylistsFolder.GetItemsInternal");
            activity?.SetTag("query.parent_id", query.ParentId.ToString());

            if (query.User is null)
            {
                query.Recursive = false;
                return base.GetItemsInternal(query);
            }

            query.Recursive = true;
            query.IncludeItemTypes = [BaseItemKind.Playlist];
            // The inherited ParentId points to AggregateFolder (22k+ descendants). Clear it so
            // LibraryManager.GetItemList skips SetTopParentIdsOrAncestors and uses our direct
            // AncestorIds anchor instead — scoping the search to PlaylistsFolder's 8 children.
            query.ParentId = Guid.Empty;
            query.AncestorIds = [Id];

            return QueryWithPostFiltering(query);
        }

        public override string GetClientTypeName()
        {
            return "ManualPlaylistsFolder";
        }
    }
}
