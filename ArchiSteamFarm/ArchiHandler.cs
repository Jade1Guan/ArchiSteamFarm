﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2017 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using ArchiSteamFarm.CMsgs;
using SteamKit2;
using SteamKit2.Internal;

namespace ArchiSteamFarm {
	internal sealed class ArchiHandler : ClientMsgHandler {
		internal const byte MaxGamesPlayedConcurrently = 32; // This is limit introduced by Steam Network

		private readonly ArchiLogger ArchiLogger;

		internal DateTime LastPacketReceived { get; private set; } = DateTime.MinValue;

		internal ArchiHandler(ArchiLogger archiLogger) => ArchiLogger = archiLogger ?? throw new ArgumentNullException(nameof(archiLogger));

		public override void HandleMsg(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			LastPacketReceived = DateTime.UtcNow;

			switch (packetMsg.MsgType) {
				case EMsg.ClientFSOfflineMessageNotification:
					HandleFSOfflineMessageNotification(packetMsg);
					break;
				case EMsg.ClientItemAnnouncements:
					HandleItemAnnouncements(packetMsg);
					break;
				case EMsg.ClientPlayingSessionState:
					HandlePlayingSessionState(packetMsg);
					break;
				case EMsg.ClientPurchaseResponse:
					HandlePurchaseResponse(packetMsg);
					break;
				case EMsg.ClientRedeemGuestPassResponse:
					HandleRedeemGuestPassResponse(packetMsg);
					break;
				case EMsg.ClientSharedLibraryLockStatus:
					HandleSharedLibraryLockStatus(packetMsg);
					break;
				case EMsg.ClientUserNotifications:
					HandleUserNotifications(packetMsg);
					break;
			}
		}

		internal void AcceptClanInvite(ulong clanID, bool accept) {
			if (clanID == 0) {
				ArchiLogger.LogNullError(nameof(clanID));
				return;
			}

			if (!Client.IsConnected) {
				return;
			}

			ClientMsg<CMsgClientClanInviteAction> request = new ClientMsg<CMsgClientClanInviteAction> {
				Body = {
					ClanID = clanID,
					AcceptInvite = accept
				}
			};

			Client.Send(request);
		}

		internal async Task PlayGames(IEnumerable<uint> gameIDs, string gameName = null) {
			if (gameIDs == null) {
				ArchiLogger.LogNullError(nameof(gameIDs));
				return;
			}

			if (!Client.IsConnected) {
				return;
			}

			ClientMsgProtobuf<CMsgClientGamesPlayed> request = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

			if (!string.IsNullOrEmpty(gameName)) {
				// If we have custom name to display, we must workaround the Steam network fuckup and send request on clean non-playing session
				// This ensures that custom name will in fact display properly
				Client.Send(request);
				await Task.Delay(Bot.CallbackSleep).ConfigureAwait(false);

				request.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
					game_extra_info = gameName,
					game_id = new GameID {
						AppType = GameID.GameType.Shortcut,
						ModID = uint.MaxValue
					}
				});
			}

			foreach (uint gameID in gameIDs.Where(gameID => gameID != 0)) {
				request.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
					game_id = new GameID(gameID)
				});
			}

			Client.Send(request);
		}

		internal async Task<RedeemGuestPassResponseCallback> RedeemGuestPass(ulong guestPassID) {
			if (guestPassID == 0) {
				ArchiLogger.LogNullError(nameof(guestPassID));
				return null;
			}

			if (!Client.IsConnected) {
				return null;
			}

			ClientMsgProtobuf<CMsgClientRedeemGuestPass> request = new ClientMsgProtobuf<CMsgClientRedeemGuestPass>(EMsg.ClientRedeemGuestPass) {
				SourceJobID = Client.GetNextJobID(),
				Body = { guest_pass_id = guestPassID }
			};

			Client.Send(request);

			try {
				return await new AsyncJob<RedeemGuestPassResponseCallback>(Client, request.SourceJobID);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return null;
			}
		}

		internal async Task<PurchaseResponseCallback> RedeemKey(string key) {
			if (string.IsNullOrEmpty(key)) {
				ArchiLogger.LogNullError(nameof(key));
				return null;
			}

			if (!Client.IsConnected) {
				return null;
			}

			ClientMsgProtobuf<CMsgClientRegisterKey> request = new ClientMsgProtobuf<CMsgClientRegisterKey>(EMsg.ClientRegisterKey) {
				SourceJobID = Client.GetNextJobID(),
				Body = { key = key }
			};

			Client.Send(request);

			try {
				return await new AsyncJob<PurchaseResponseCallback>(Client, request.SourceJobID);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return null;
			}
		}

		private void HandleFSOfflineMessageNotification(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientOfflineMessageNotification> response = new ClientMsgProtobuf<CMsgClientOfflineMessageNotification>(packetMsg);
			Client.PostCallback(new OfflineMessageCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleItemAnnouncements(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientItemAnnouncements> response = new ClientMsgProtobuf<CMsgClientItemAnnouncements>(packetMsg);
			Client.PostCallback(new NotificationsCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandlePlayingSessionState(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientPlayingSessionState> response = new ClientMsgProtobuf<CMsgClientPlayingSessionState>(packetMsg);
			Client.PostCallback(new PlayingSessionStateCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandlePurchaseResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientPurchaseResponse> response = new ClientMsgProtobuf<CMsgClientPurchaseResponse>(packetMsg);
			Client.PostCallback(new PurchaseResponseCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleRedeemGuestPassResponse(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientRedeemGuestPassResponse> response = new ClientMsgProtobuf<CMsgClientRedeemGuestPassResponse>(packetMsg);
			Client.PostCallback(new RedeemGuestPassResponseCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleSharedLibraryLockStatus(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientSharedLibraryLockStatus> response = new ClientMsgProtobuf<CMsgClientSharedLibraryLockStatus>(packetMsg);
			Client.PostCallback(new SharedLibraryLockStatusCallback(packetMsg.TargetJobID, response.Body));
		}

		private void HandleUserNotifications(IPacketMsg packetMsg) {
			if (packetMsg == null) {
				ArchiLogger.LogNullError(nameof(packetMsg));
				return;
			}

			ClientMsgProtobuf<CMsgClientUserNotifications> response = new ClientMsgProtobuf<CMsgClientUserNotifications>(packetMsg);
			Client.PostCallback(new NotificationsCallback(packetMsg.TargetJobID, response.Body));
		}

		internal sealed class NotificationsCallback : CallbackMsg {
			internal readonly HashSet<ENotification> Notifications;

			internal NotificationsCallback(JobID jobID, CMsgClientUserNotifications msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;

				if (msg.notifications.Count == 0) {
					return;
				}

				Notifications = new HashSet<ENotification>(msg.notifications.Select(notification => (ENotification) notification.user_notification_type));
			}

			internal NotificationsCallback(JobID jobID, CMsgClientItemAnnouncements msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;

				if (msg.count_new_items > 0) {
					Notifications = new HashSet<ENotification> {
						ENotification.Items
					};
				}
			}

			internal enum ENotification : byte {
				[SuppressMessage("ReSharper", "UnusedMember.Global")]
				Unknown = 0,

				Trading = 1,

				// Only custom below, different than ones available as user_notification_type
				Items = 254
			}
		}

		internal sealed class OfflineMessageCallback : CallbackMsg {
			internal readonly uint OfflineMessagesCount;
			internal readonly HashSet<uint> Steam3IDs;

			internal OfflineMessageCallback(JobID jobID, CMsgClientOfflineMessageNotification msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				OfflineMessagesCount = msg.offline_messages;

				if (msg.friends_with_offline_messages == null) {
					return;
				}

				Steam3IDs = new HashSet<uint>(msg.friends_with_offline_messages);
			}
		}

		internal sealed class PlayingSessionStateCallback : CallbackMsg {
			internal readonly bool PlayingBlocked;

			internal PlayingSessionStateCallback(JobID jobID, CMsgClientPlayingSessionState msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				PlayingBlocked = msg.playing_blocked;
			}
		}

		internal sealed class PurchaseResponseCallback : CallbackMsg {
			internal readonly Dictionary<uint, string> Items;

			internal EPurchaseResultDetail PurchaseResultDetail { get; set; }
			internal EResult Result { get; set; }

			internal PurchaseResponseCallback(JobID jobID, CMsgClientPurchaseResponse msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				PurchaseResultDetail = (EPurchaseResultDetail) msg.purchase_result_details;
				Result = (EResult) msg.eresult;

				if (msg.purchase_receipt_info == null) {
					ASF.ArchiLogger.LogNullError(nameof(msg.purchase_receipt_info));
					return;
				}

				KeyValue receiptInfo = new KeyValue();
				using (MemoryStream ms = new MemoryStream(msg.purchase_receipt_info)) {
					if (!receiptInfo.TryReadAsBinary(ms)) {
						ASF.ArchiLogger.LogNullError(nameof(ms));
						return;
					}
				}

				List<KeyValue> lineItems = receiptInfo["lineitems"].Children;
				if (lineItems.Count == 0) {
					return;
				}

				Items = new Dictionary<uint, string>(lineItems.Count);
				foreach (KeyValue lineItem in lineItems) {
					uint packageID = lineItem["PackageID"].AsUnsignedInteger();
					if (packageID == 0) {
						// Coupons have PackageID of -1 (don't ask me why)
						// We'll use ItemAppID in this case
						packageID = lineItem["ItemAppID"].AsUnsignedInteger();
						if (packageID == 0) {
							ASF.ArchiLogger.LogNullError(nameof(packageID));
							return;
						}
					}

					string gameName = lineItem["ItemDescription"].Value;
					if (string.IsNullOrEmpty(gameName)) {
						ASF.ArchiLogger.LogNullError(nameof(gameName));
						return;
					}

					// Apparently steam expects client to decode sent HTML
					gameName = WebUtility.HtmlDecode(gameName);
					Items[packageID] = gameName;
				}
			}
		}

		internal sealed class RedeemGuestPassResponseCallback : CallbackMsg {
			internal readonly EResult Result;

			internal RedeemGuestPassResponseCallback(JobID jobID, CMsgClientRedeemGuestPassResponse msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;
				Result = (EResult) msg.eresult;
			}
		}

		internal sealed class SharedLibraryLockStatusCallback : CallbackMsg {
			internal readonly ulong LibraryLockedBySteamID;

			internal SharedLibraryLockStatusCallback(JobID jobID, CMsgClientSharedLibraryLockStatus msg) {
				if ((jobID == null) || (msg == null)) {
					throw new ArgumentNullException(nameof(jobID) + " || " + nameof(msg));
				}

				JobID = jobID;

				if (msg.own_library_locked_by == 0) {
					return;
				}

				LibraryLockedBySteamID = new SteamID(msg.own_library_locked_by, EUniverse.Public, EAccountType.Individual);
			}
		}
	}
}