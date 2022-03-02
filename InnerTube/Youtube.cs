using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using InnerTube.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InnerTube
{
	public class Youtube
	{
		internal readonly HttpClient Client = new();

		public readonly Dictionary<string, CacheItem<YoutubePlayer>> PlayerCache = new();

		private async Task<JObject> MakeRequest(string endpoint, Dictionary<string, object> postData)
		{
			HttpRequestMessage hrm = new(HttpMethod.Post,
				@$"https://www.youtube.com/youtubei/v1/{endpoint}?key=AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8");

			byte[] buffer = Encoding.UTF8.GetBytes(RequestContext.BuildRequestContextJson(postData));
			ByteArrayContent byteContent = new(buffer);
			byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			hrm.Content = byteContent;
			HttpResponseMessage ytPlayerRequest = await Client.SendAsync(hrm);

			return JObject.Parse(await ytPlayerRequest.Content.ReadAsStringAsync());
		}

		public async Task<YoutubePlayer> GetPlayerAsync(string videoId)
		{
			if (PlayerCache.Any(x => x.Key == videoId && x.Value.ExpireTime > DateTimeOffset.Now))
			{
				CacheItem<YoutubePlayer> item = PlayerCache[videoId];
				item.Item.ExpiresInSeconds = (item.ExpireTime - DateTimeOffset.Now).TotalSeconds.ToString(); 
				return item.Item;
			}

			YoutubePlayer player = await YtDlp.GetVideo(videoId).GetYoutubePlayer();
			PlayerCache.Add(videoId,
				new CacheItem<YoutubePlayer>(player,
					TimeSpan.FromSeconds(int.Parse(player.ExpiresInSeconds)).Subtract(TimeSpan.FromHours(1))));
			return player;
		}

		public async Task<YoutubeVideo> GetVideoAsync(string videoId)
		{
			JObject player = await MakeRequest("next", new Dictionary<string, object>
			{
				["videoId"] = videoId
			});

			YoutubeVideo video = new()
			{
				Id = player?["currentVideoEndpoint"]?["watchEndpoint"]?["videoId"]?.ToString(),
				Title = Utils.ReadRuns(
					player?["contents"]?["twoColumnWatchNextResults"]?["results"]?["results"]?["contents"]?[0]?
						["videoPrimaryInfoRenderer"]?["title"]?["runs"]?.ToObject<JArray>()),
				Description =
					Utils.ReadRuns(
						player?["contents"]?["twoColumnWatchNextResults"]?["results"]?["results"]?["contents"]?[1]?
							["videoSecondaryInfoRenderer"]?["description"]?["runs"]?.ToObject<JArray>()),
				Channel = new Channel
				{
					Name =
						player?["contents"]?["twoColumnWatchNextResults"]?["results"]?["results"]?["contents"]?[1]?
							["videoSecondaryInfoRenderer"]?["owner"]?["videoOwnerRenderer"]?["title"]?["runs"]?[0]?[
								"text"]?.ToString(),
					Id = player?["contents"]?["twoColumnWatchNextResults"]?["results"]?["results"]?["contents"]?[1]?
						["videoSecondaryInfoRenderer"]?["owner"]?["videoOwnerRenderer"]?["title"]?["runs"]?[0]?
						["navigationEndpoint"]?["browseEndpoint"]?["browseId"]?.ToString(),
					SubscriberCount =
						player?["contents"]?["twoColumnWatchNextResults"]?["results"]?["results"]?["contents"]?[1]?
							["videoSecondaryInfoRenderer"]?["owner"]?["videoOwnerRenderer"]?["subscriberCountText"]?[
								"simpleText"]?.ToString(),
					Avatars =
						(player?["contents"]?["twoColumnWatchNextResults"]?["results"]?["results"]?["contents"]?[1]?[
								"videoSecondaryInfoRenderer"]?["owner"]?["videoOwnerRenderer"]?["thumbnail"]?[
								"thumbnails"]
							?.ToObject<JArray>() ?? new JArray()).Select(Utils.ParseThumbnails).ToArray()
				},
				UploadDate =
					player?["contents"]?["twoColumnWatchNextResults"]?["results"]?["results"]?["contents"]?[0]?[
						"videoPrimaryInfoRenderer"]?["dateText"]?["simpleText"]?.ToString(),
				Recommended =
					ParseRenderers(
						player?["contents"]?["twoColumnWatchNextResults"]?["secondaryResults"]?["secondaryResults"]?
							["results"]?.ToObject<JArray>() ?? new JArray())
			};

			return video;
		}

		public async Task<YoutubeSearchResults> SearchAsync(string query, string continuation = null)
		{
			Dictionary<string, object> data = new();
			if (string.IsNullOrWhiteSpace(continuation))
				data.Add("query", query);
			else
				data.Add("continuation", continuation);
			JObject search = await MakeRequest("search", data);

			return new YoutubeSearchResults
			{
				Refinements = search?["refinements"]?.ToObject<string[]>() ?? Array.Empty<string>(),
				EstimatedResults = search?["estimatedResults"]?.ToObject<int>() ?? 0,
				Results = ParseRenderers(
					search?["contents"]?["twoColumnSearchResultsRenderer"]?["primaryContents"]?["sectionListRenderer"]?
						["contents"]?[0]?["itemSectionRenderer"]?["contents"]?.ToObject<JArray>() ??
					search?["onResponseReceivedCommands"]?[0]?["appendContinuationItemsAction"]?["continuationItems"]?
						[0]?["itemSectionRenderer"]?["contents"]?.ToObject<JArray>() ?? new JArray()),
				ContinuationKey =
					search?["contents"]?["twoColumnSearchResultsRenderer"]?["primaryContents"]?["sectionListRenderer"]?
						["contents"]?[1]?["continuationItemRenderer"]?["continuationEndpoint"]?["continuationCommand"]?
						["token"]?.ToString() ??
					search?["onResponseReceivedCommands"]?[0]?["appendContinuationItemsAction"]?["continuationItems"]?
						[1]?["continuationItemRenderer"]?["continuationEndpoint"]?["continuationCommand"]?["token"]
						?.ToString() ?? ""
			};
		}

		public async Task<YoutubePlaylist> GetPlaylistAsync(string id, string continuation = null)
		{
			Dictionary<string, object> data = new();
			if (string.IsNullOrWhiteSpace(continuation))
				data.Add("browseId", "VL" + id);
			else
				data.Add("continuation", continuation);
			JObject playlist = await MakeRequest("browse", data);
			DynamicItem[] renderers = ParseRenderers(
				playlist?["contents"]?["twoColumnBrowseResultsRenderer"]?["tabs"]?[0]?["tabRenderer"]?["content"]?
					["sectionListRenderer"]?["contents"]?[0]?["itemSectionRenderer"]?["contents"]?[0]?
					["playlistVideoListRenderer"]?["contents"]?.ToObject<JArray>() ??
				playlist?["onResponseReceivedActions"]?[0]?["appendContinuationItemsAction"]?["continuationItems"]
					?.ToObject<JArray>() ?? new JArray());

			YoutubePlaylist @async = new YoutubePlaylist();
			async.Title = playlist?["metadata"]?["playlistMetadataRenderer"]?["title"]?.ToString();
			async.Description = playlist?["metadata"]?["playlistMetadataRenderer"]?["description"]?.ToString();
			async.VideoCount = playlist?["sidebar"]?["playlistSidebarRenderer"]?["items"]?[0]?[
				"playlistSidebarPrimaryInfoRenderer"]?["stats"]?[0]?["runs"]?[0]?["text"]?.ToString();
			async.ViewCount = playlist?["sidebar"]?["playlistSidebarRenderer"]?["items"]?[0]?[
				"playlistSidebarPrimaryInfoRenderer"]?["stats"]?[1]?["simpleText"]?.ToString();
			async.LastUpdated = Utils.ReadRuns(playlist?["sidebar"]?["playlistSidebarRenderer"]?["items"]?[0]?[
				"playlistSidebarPrimaryInfoRenderer"]?["stats"]?[2]?["runs"]?.ToObject<JArray>() ?? new JArray());
			async.Thumbnail = (playlist?["microformat"]?["microformatDataRenderer"]?["thumbnail"]?["thumbnails"] ??
			                   new JArray()).Select(Utils.ParseThumbnails).ToArray();
			async.Channel = new Channel
			{
				Name =
					playlist?["sidebar"]?["playlistSidebarRenderer"]?["items"]?[1]?
						["playlistSidebarSecondaryInfoRenderer"]?["videoOwner"]?["videoOwnerRenderer"]?["title"]?
						["runs"]?[0]?["text"]?.ToString(),
				Id = playlist?["sidebar"]?["playlistSidebarRenderer"]?["items"]?[1]?
					["playlistSidebarSecondaryInfoRenderer"]?["videoOwner"]?["videoOwnerRenderer"]?
					["navigationEndpoint"]?["browseEndpoint"]?["browseId"]?.ToString(),
				SubscriberCount = "",
				Avatars =
					(playlist?["sidebar"]?["playlistSidebarRenderer"]?["items"]?[1]?
						["playlistSidebarSecondaryInfoRenderer"]?["videoOwner"]?["videoOwnerRenderer"]?["thumbnail"]
						?["thumbnails"] ?? new JArray()).Select(Utils.ParseThumbnails).ToArray()
			};
			async.Videos = renderers.Where(x => x is not ContinuationItem).ToArray();
			async.ContinuationKey = renderers.FirstOrDefault(x => x is ContinuationItem)?.Id;
			return async;
		}

		/*
		public async Task<YoutubeChannel> GetChannelAsync(string query, string continuation = null)
		{
			string jsonDoc = continuation == null
				? await Client.GetStringAsync("/channel?id=" + query)
				: await Client.GetStringAsync("/channel?continuation=" + continuation);
			return JsonConvert.DeserializeObject<YoutubeChannel>(jsonDoc);
		}
		*/

		private DynamicItem[] ParseRenderers(JArray renderersArray)
		{
			List<DynamicItem> items = new();

			foreach (JToken jToken in renderersArray)
			{
				JObject recommendationContainer = jToken as JObject;
				string rendererName = recommendationContainer?.First?.Path.Split(".").Last() ?? "";
				JObject rendererItem = recommendationContainer?[rendererName]?.ToObject<JObject>();
				switch (rendererName)
				{
					case "videoRenderer":
						items.Add(new VideoItem
						{
							Id = rendererItem?["videoId"]?.ToString(),
							Title = Utils.ReadRuns(rendererItem?["title"]?["runs"]?.ToObject<JArray>() ??
							                       new JArray()),
							Thumbnails =
								(rendererItem?["thumbnail"]?["thumbnails"]?.ToObject<JArray>() ??
								 new JArray()).Select(Utils.ParseThumbnails).ToArray(),
							UploadedAt = rendererItem?["publishedTimeText"]?["simpleText"]?.ToString(),
							Views = int.Parse(
								rendererItem?["viewCountText"]?["simpleText"]?.ToString().Split(" ")[0]
									.Replace(",", "").Replace(".", "") ?? "0"),
							Channel = new Channel
							{
								Name = rendererItem?["longBylineText"]?["runs"]?[0]?["text"]?.ToString(),
								Id = rendererItem?["longBylineText"]?["runs"]?[0]?["navigationEndpoint"]?[
									"browseEndpoint"]?["browseId"]?.ToString(),
								SubscriberCount = null,
								Avatars =
									(rendererItem?["channelThumbnailSupportedRenderers"]?[
											"channelThumbnailWithLinkRenderer"]?["thumbnail"]?["thumbnails"]
										?.ToObject<JArray>() ?? new JArray()).Select(Utils.ParseThumbnails)
									.ToArray()
							},
							Duration = rendererItem?["thumbnailOverlays"]?[0]?[
								"thumbnailOverlayTimeStatusRenderer"]?["text"]?["simpleText"]?.ToString(),
							Description = Utils.ReadRuns(rendererItem?["detailedMetadataSnippets"]?[0]?[
								"snippetText"]?["runs"]?.ToObject<JArray>() ?? new JArray())
						});
						break;
					case "playlistRenderer":
						items.Add(new PlaylistItem
						{
							Id = rendererItem?["playlistId"]
								?.ToString(),
							Title = rendererItem?["title"]?["simpleText"]
								?.ToString(),
							Thumbnails =
								(rendererItem?["thumbnails"]?[0]?["thumbnails"]?.ToObject<JArray>() ??
								 new JArray()).Select(Utils.ParseThumbnails).ToArray(),
							VideoCount = int.Parse(
								rendererItem?["videoCountText"]?["runs"]?[0]?["text"]?.ToString().Replace(",", "")
									.Replace(".", "") ?? "0"),
							FirstVideoId = rendererItem?["navigationEndpoint"]?["watchEndpoint"]?["videoId"]
								?.ToString(),
							Channel = new Channel
							{
								Name = rendererItem?["longBylineText"]?["runs"]?[0]?["text"]
									?.ToString(),
								Id = rendererItem?["longBylineText"]?["runs"]?[0]?["navigationEndpoint"]?[
										"browseEndpoint"]?["browseId"]
									?.ToString(),
								SubscriberCount = null,
								Avatars = null
							}
						});
						break;
					case "channelRenderer":
						items.Add(new ChannelItem
						{
							Id = rendererItem?["channelId"]?.ToString(),
							Title = rendererItem?["title"]?["simpleText"]?.ToString(),
							Thumbnails =
								(rendererItem?["thumbnail"]?["thumbnails"]
									 ?.ToObject<JArray>() ??
								 new JArray()).Select(Utils.ParseThumbnails)
								.ToArray(), //
							Url = rendererItem?["navigationEndpoint"]?["commandMetadata"]?["webCommandMetadata"]?["url"]
								?.ToString(),
							Description =
								Utils.ReadRuns(rendererItem?["descriptionSnippet"]?["runs"]?.ToObject<JArray>() ??
								               new JArray()),
							VideoCount = int.Parse(
								rendererItem?["videoCountText"]?["runs"]?[0]?["text"]
									?.ToString()
									.Replace(",",
										"")
									.Replace(".",
										"") ??
								"0"),
							Subscribers = rendererItem?["subscriberCountText"]?["simpleText"]?.ToString()
						});
						break;
					case "radioRenderer":
						items.Add(new RadioItem
						{
							Id = rendererItem?["playlistId"]
								?.ToString(),
							Title = rendererItem?["title"]?["simpleText"]
								?.ToString(),
							Thumbnails =
								(rendererItem?["thumbnails"]?[0]?["thumbnails"]?.ToObject<JArray>() ??
								 new JArray()).Select(Utils.ParseThumbnails).ToArray(),
							FirstVideoId = rendererItem?["navigationEndpoint"]?["watchEndpoint"]?["videoId"]
								?.ToString(),
							Channel = new Channel
							{
								Name = rendererItem?["longBylineText"]?["simpleText"]?.ToString(),
								Id = "",
								SubscriberCount = null,
								Avatars = null
							}
						});
						break;
					case "shelfRenderer":
						items.Add(new ShelfItem
						{
							Title = rendererItem?["title"]?["simpleText"]
								?.ToString(),
							Items = ParseRenderers(
								rendererItem?["content"]?["verticalListRenderer"]?["items"]?.ToObject<JArray>() ??
								new JArray()),
							CollapsedItemCount =
								rendererItem?["content"]?["verticalListRenderer"]?["collapsedItemCount"]
									?.ToObject<int>() ?? 0
						});
						break;
					case "horizontalCardListRenderer":
						items.Add(new HorizontalCardListItem
						{
							Title = rendererItem?["header"]?["richListHeaderRenderer"]?["title"]?["simpleText"]
								?.ToString(),
							Items = ParseRenderers(rendererItem?["cards"]?.ToObject<JArray>() ?? new JArray())
						});
						break;
					case "searchRefinementCardRenderer":
						items.Add(new CardItem
						{
							Title = Utils.ReadRuns(rendererItem?["query"]?["runs"]?.ToObject<JArray>() ??
							                       new JArray()),
							Thumbnails = (rendererItem?["thumbnail"]?["thumbnails"]?.ToObject<JArray>() ??
							              new JArray()).Select(Utils.ParseThumbnails).ToArray()
						});
						break;
					case "compactVideoRenderer":
						items.Add(new VideoItem
						{
							Id = rendererItem?["videoId"]?.ToString(),
							Title = rendererItem?["title"]?["simpleText"]?.ToString(),
							Thumbnails =
								(rendererItem?["thumbnail"]?["thumbnails"]?.ToObject<JArray>() ??
								 new JArray()).Select(Utils.ParseThumbnails).ToArray(),
							UploadedAt = rendererItem?["publishedTimeText"]?["simpleText"]?.ToString(),
							Views = int.Parse(
								rendererItem?["viewCountText"]?["simpleText"]?.ToString().Split(" ")[0]
									.Replace(",", "").Replace(".", "") ?? "0"),
							Channel = new Channel
							{
								Name = rendererItem?["longBylineText"]?["runs"]?[0]?["text"]?.ToString(),
								Id = rendererItem?["longBylineText"]?["runs"]?[0]?["navigationEndpoint"]?[
									"browseEndpoint"]?["browseId"]?.ToString(),
								SubscriberCount = null,
								Avatars = null
							},
							Duration = rendererItem?["thumbnailOverlays"]?[0]?[
								"thumbnailOverlayTimeStatusRenderer"]?["text"]?["simpleText"]?.ToString()
						});
						break;
					case "compactPlaylistRenderer":
						items.Add(new PlaylistItem
						{
							Id = rendererItem?["playlistId"]
								?.ToString(),
							Title = rendererItem?["title"]?["simpleText"]
								?.ToString(),
							Thumbnails =
								(rendererItem?["thumbnail"]?["thumbnails"]
									?.ToObject<JArray>() ?? new JArray()).Select(Utils.ParseThumbnails)
								.ToArray(),
							VideoCount = int.Parse(
								rendererItem?["videoCountText"]?["runs"]?[0]?["text"]?.ToString().Replace(",", "")
									.Replace(".", "") ?? "0"),
							FirstVideoId = rendererItem?["navigationEndpoint"]?["watchEndpoint"]?["videoId"]
								?.ToString(),
							Channel = new Channel
							{
								Name = rendererItem?["longBylineText"]?["runs"]?[0]?["text"]
									?.ToString(),
								Id = rendererItem?["longBylineText"]?["runs"]?[0]?["navigationEndpoint"]?[
										"browseEndpoint"]?["browseId"]
									?.ToString(),
								SubscriberCount = null,
								Avatars = null
							}
						});
						break;
					case "compactRadioRenderer":
						items.Add(new RadioItem
						{
							Id = rendererItem?["playlistId"]
								?.ToString(),
							Title = rendererItem?["title"]?["simpleText"]
								?.ToString(),
							Thumbnails =
								(rendererItem?["thumbnail"]?["thumbnails"]
									?.ToObject<JArray>() ?? new JArray()).Select(Utils.ParseThumbnails)
								.ToArray(),
							FirstVideoId = rendererItem?["navigationEndpoint"]?["watchEndpoint"]?["videoId"]
								?.ToString(),
							Channel = new Channel
							{
								Name = rendererItem?["longBylineText"]?["simpleText"]?.ToString(),
								Id = "",
								SubscriberCount = null,
								Avatars = null
							}
						});
						break;
					case "continuationItemRenderer":
						items.Add(new ContinuationItem
						{
							Id = rendererItem?["continuationEndpoint"]?["continuationCommand"]?["token"]?.ToString()
						});
						break;
					case "playlistVideoRenderer":
						items.Add(new PlaylistVideoItem
						{
							Id = rendererItem?["videoId"]?.ToString(),
							Index = rendererItem?["index"]?["simpleText"]?.ToObject<long>() ?? 0,
							Title = Utils.ReadRuns(rendererItem?["title"]?["runs"]?.ToObject<JArray>() ??
							                       new JArray()),
							Thumbnails =
								(rendererItem?["thumbnail"]?["thumbnails"]?.ToObject<JArray>() ??
								 new JArray()).Select(Utils.ParseThumbnails).ToArray(),
							Channel = new Channel
							{
								Name = rendererItem?["shortBylineText"]?["runs"]?[0]?["text"]?.ToString(),
								Id = rendererItem?["shortBylineText"]?["runs"]?[0]?["navigationEndpoint"]?[
									"browseEndpoint"]?["browseId"]?.ToString(),
								SubscriberCount = null,
								Avatars = null
							},
							Duration = rendererItem?["lengthText"]?["simpleText"]?.ToString()
						});
						break;
					default:
						#if DEBUG
						items.Add(new DynamicItem
						{
							Id = rendererName,
							Title = rendererItem?.ToString()
						});
						#endif
						break;
				}
			}

			return items.ToArray();
		}
	}
}