﻿#region Copyright

// --------------------------------------------------------------------------------------------------------------------
// <copyright file="CommandService.cs">
//    Copyright (c) 2013, Justin Kadrovach, All rights reserved.
//   
//    This source is subject to the Simplified BSD License.
//    Please see the License.txt file for more information.
//    All other rights reserved.
//    
//    THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY 
//    KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
//    IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
//    PARTICULAR PURPOSE.
// </copyright>
//  --------------------------------------------------------------------------------------------------------------------

#endregion

namespace slimCat.Services
{
    #region Usings

    using Microsoft.Practices.Prism.Events;
    using Microsoft.Practices.Prism.Regions;
    using Microsoft.Practices.Unity;
    using Models;
    using SimpleJson;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Timers;
    using System.Web;
    using System.Windows;
    using Utilities;
    using ViewModels;
    using Commands = Utilities.Constants.ServerCommands;

    #endregion

    /// <summary>
    ///     This interprets the commands and translates them to methods that our various other services can use.
    ///     It also coordinates them to prevent collisions.
    ///     This intercepts just about every single command that the server sends.
    /// </summary>
    public class CommandService : ViewModelBase
    {
        #region Fields

        private readonly IAutomationService automation;

        private readonly IChatConnection connection;

        private readonly object locker = new object();

        private readonly IChannelManager manager;

        private readonly INoteService notes;

        private readonly string[] noisyTypes;

        private readonly Queue<IDictionary<string, object>> que = new Queue<IDictionary<string, object>>();

        private readonly HashSet<string> autoJoinedChannels = new HashSet<string>();

        #endregion

        #region Constructors and Destructors

        public CommandService(
            IChatModel cm,
            IChatConnection conn,
            IChannelManager manager,
            IUnityContainer contain,
            IRegionManager regman,
            IEventAggregator eventagg,
            ICharacterManager characterManager,
            IAutomationService automation,
            INoteService notes)
            : base(contain, regman, eventagg, cm, characterManager)
        {
            connection = conn;
            this.manager = manager;
            this.automation = automation;
            this.notes = notes;

            Events.GetEvent<CharacterSelectedLoginEvent>()
                .Subscribe(GetCharacter, ThreadOption.BackgroundThread, true);
            Events.GetEvent<ChatCommandEvent>().Subscribe(EnqueueAction, ThreadOption.BackgroundThread, true);
            Events.GetEvent<ConnectionClosedEvent>().Subscribe(WipeState, ThreadOption.PublisherThread, true);

            ChatModel.CurrentAccount = connection.Account;

            noisyTypes = new[]
                {
                    Commands.UserJoin,
                    Commands.UserLeave,
                    Commands.UserStatus,
                    Commands.PublicChannelList,
                    Commands.PrivateChannelList,
                    Commands.UserList,
                    Commands.ChannelAd,
                    Commands.ChannelMessage
                };

            LoggingSection = "cmnd serv";
        }

        #endregion

        #region Delegates

        /// <summary>
        ///     The command delegate.
        /// </summary>
        /// <param name="command">
        ///     The command.
        /// </param>
        private delegate void CommandDelegate(IDictionary<string, object> command);

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     The initialize.
        /// </summary>
        public override void Initialize()
        {
        }

        #endregion

        #region Methods

        private static Gender ParseGender(string input)
        {
            switch (input)
            {
                    // manually determine some really annoyingly-named genders
                case "Male-Herm":
                    return Gender.HermM;

                case "Herm":
                    return Gender.HermF;

                case "Cunt-boy":
                    return Gender.Cuntboy;

                default: // every other gender is parsed normally
                    return input.ToEnum<Gender>();
            }
        }

        private void AdMessageCommand(IDictionary<string, object> command)
        {
            MessageReceived(command, true);
        }

        private void AdminsCommand(IDictionary<string, object> command)
        {
            CharacterManager.Set(command.Get<JsonArray>(Constants.Arguments.MultipleModerators), ListKind.Moderator);
            if (CharacterManager.IsOnList(ChatModel.CurrentCharacter.Name, ListKind.Moderator, false))
                Dispatcher.Invoke((Action) delegate { ChatModel.IsGlobalModerator = true; });
        }

        private void BroadcastCommand(IDictionary<string, object> command)
        {
            var message = command.Get(Constants.Arguments.Message);

            if (!command.ContainsKey(Constants.Arguments.Character))
            {
                ErrorCommand(command);
                return;
            }

            var posterName = command.Get(Constants.Arguments.Character);
            var poster = CharacterManager.Find(posterName);

            // message should be in the format:
            // [b]Broadcast from username:[/b] message
            // but this is redundant with slimCat, so cut out the first bit
            var indexOfClosingTag = message.IndexOf("[/b]", StringComparison.OrdinalIgnoreCase);

            if (indexOfClosingTag != -1)
                message = message.Substring(indexOfClosingTag + "[/b] ".Length);

            Events.GetEvent<NewUpdateEvent>()
                .Publish(
                    new CharacterUpdateModel(poster, new CharacterUpdateModel.BroadcastEventArgs {Message = message}));
        }

        private void ChannelBanListCommand(IDictionary<string, object> command)
        {
            var channelId = command.Get(Constants.Arguments.Channel);
            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelId);

            if (channel == null)
            {
                RequeueCommand(command);
                return;
            }

            var message = channelId.Split(':');
            var banned = message[1].Trim();

            if (banned.IndexOf(',') == -1)
                channel.CharacterManager.Add(banned, ListKind.Banned);
            else
                channel.CharacterManager.Set(banned.Split(','), ListKind.Banned);

            Events.GetEvent<NewUpdateEvent>()
                .Publish(new ChannelUpdateModel(channel, new ChannelUpdateModel.ChannelTypeBannedListEventArgs()));
        }

        private void ChannelDescriptionCommand(IDictionary<string, object> command)
        {
            var channelName = command.Get(Constants.Arguments.Channel);
            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelName) ??
                          ChatModel.AllChannels.FirstByIdOrNull(channelName);
            var description = command.Get("description");

            if (channel == null)
            {
                RequeueCommand(command);
                return;
            }

            var isInitializer = string.IsNullOrWhiteSpace(channel.Description);

            if (string.Equals(channel.Description, description, StringComparison.Ordinal))
                return;

            channel.Description = description;

            if (isInitializer)
                return;

            var args = new ChannelUpdateModel.ChannelDescriptionChangedEventArgs();
            Events.GetEvent<NewUpdateEvent>().Publish(new ChannelUpdateModel(channel, args));
        }

        private void ChannelInitializedCommand(IDictionary<string, object> command)
        {
            var channelName = command.Get(Constants.Arguments.Channel);
            var mode = command.Get(Constants.Arguments.Mode).ToEnum<ChannelMode>();
            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelName) ??
                          ChatModel.AllChannels.FirstByIdOrNull(channelName);

            if (channel == null)
            {
                RequeueCommand(command);
                return;
            }

            channel.Mode = mode;
            var users = (JsonArray) command[Constants.Arguments.MultipleUsers];
            foreach (IDictionary<string, object> character in users)
            {
                var name = character.Get(Constants.Arguments.Identity);

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                channel.CharacterManager.SignOn(CharacterManager.Find(name));
            }
        }

        private void ChannelListCommand(IDictionary<string, object> command, bool isPublic)
        {
            var arr = (JsonArray) command[Constants.Arguments.MultipleChannels];
            lock (ChatModel.AllChannels)
            {
                foreach (IDictionary<string, object> channel in arr)
                {
                    var name = channel.Get(Constants.Arguments.Name);
                    string title = null;
                    if (!isPublic)
                        title = HttpUtility.HtmlDecode(channel.Get(Constants.Arguments.Title));

                    var mode = ChannelMode.Both;
                    if (isPublic)
                        mode = channel.Get(Constants.Arguments.Mode).ToEnum<ChannelMode>();

                    var number = (long) channel[Constants.Arguments.MultipleCharacters];
                    if (number < 0)
                        number = 0;

                    var model = new GeneralChannelModel(name, isPublic ? ChannelType.Public : ChannelType.Private, (int)number, mode)
                    {
                        Title = isPublic ? name : title
                    };

                    Dispatcher.Invoke((Action) (() =>
                        {
                            var current = ChatModel.AllChannels.FirstByIdOrNull(name);
                            if (current == null)
                            {
                                ChatModel.AllChannels.Add(model);
                                return;
                            }

                            current.Mode = mode;
                            current.UserCount = (int)number;
                        }));

                }
            }
        }

        private void ChannelMessageCommand(IDictionary<string, object> command)
        {
            MessageReceived(command, false);
        }

        private void ChannelOperatorListCommand(IDictionary<string, object> command)
        {
            var channelName = command.Get(Constants.Arguments.Channel);
            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelName)
                        ?? ChatModel.AllChannels.FirstByIdOrNull(channelName);

            if (channel == null)
            {
                RequeueCommand(command);
                return;
            }

            channel.CharacterManager.Set(command.Get<JsonArray>("oplist"), ListKind.Moderator);
        }

        private void CharacterDisconnectCommand(IDictionary<string, object> command)
        {
            var characterName = (string) command[Constants.Arguments.Character];

            var character = CharacterManager.Find(characterName);
            var ofInterest = CharacterManager.IsOfInterest(characterName);

            character.LastAd = null;
            character.LastReport = null;

            CharacterManager.SignOff(characterName);

            var leaveChannelCommands = from channel in ChatModel.CurrentChannels
                where channel.CharacterManager.SignOff(characterName)
                select
                    new Dictionary<string, object>
                        {
                            {Constants.Arguments.Character, character.Name},
                            {Constants.Arguments.Channel, channel.Id},
                            {"ignoreUpdate", ofInterest}
                            // ignore updates from characters we'll already get a sign-out notice for
                        };

            leaveChannelCommands.Each(LeaveChannelCommand);

            var characterChannel = ChatModel.CurrentPms.FirstByIdOrNull(characterName);
            if (characterChannel != null)
                characterChannel.TypingStatus = TypingStatus.Clear;

            Events.GetEvent<NewUpdateEvent>()
                .Publish(
                    new CharacterUpdateModel(
                        character, new CharacterUpdateModel.LoginStateChangedEventArgs {IsLogIn = false}));
        }

        private void DoAction()
        {
            lock (locker)
            {
                if (que.Count <= 0)
                    return;

                var workingData = que.Dequeue();

                Invoke(workingData);
                DoAction();
            }
        }

        private void EnqueueAction(IDictionary<string, object> data)
        {
            if (data == null) return;

            if (autoJoinedChannels.Count != 0 && data.Get(Constants.Arguments.Command) == Commands.ChannelJoin)
            {
                AutoJoinChannelCommand(data);
                return;
            }
            if (data.Get(Constants.Arguments.Command) == Commands.ChannelJoin)
            {
                var characterDict = data.Get<IDictionary<string, object>>(Constants.Arguments.Character);
                var character = characterDict.Get(Constants.Arguments.Identity);

                if (character == ChatModel.CurrentCharacter.Name)
                {
                    QuickJoinChannelCommand(data);
                    return;
                }
            }

            que.Enqueue(data);
            DoAction();
        }

        private void Invoke(IDictionary<string, object> command)
        {
            var toInvoke = InterpretCommand(command);
            if (toInvoke != null)
                toInvoke.Invoke(command);
        }

        private void ErrorCommand(IDictionary<string, object> command)
        {
            var thisMessage = command.Get(Constants.Arguments.Message);

            // for some fucktarded reason room status changes are only done through SYS
            if (thisMessage.IndexOf("this channel is now", StringComparison.OrdinalIgnoreCase) != -1)
            {
                RoomTypeChangedCommand(command);
                return;
            }

            // checks to see if this is a channel ban message
            if (thisMessage.IndexOf("Channel bans", StringComparison.OrdinalIgnoreCase) != -1)
            {
                ChannelBanListCommand(command);
                return;
            }

            // checks to ensure it's not a mod promote message
            if (thisMessage.IndexOf("has been promoted", StringComparison.OrdinalIgnoreCase) == -1)
                Events.GetEvent<ErrorEvent>().Publish(thisMessage);
        }

        private void IgnoreUserCommand(IDictionary<string, object> command)
        {
            Action<string> doAction;
            if (command.Get(Constants.Arguments.Action) != Constants.Arguments.ActionDelete)
                doAction = x => CharacterManager.Add(x, ListKind.Ignored);
            else
                doAction = x => CharacterManager.Remove(x, ListKind.Ignored);

            if (command.ContainsKey(Constants.Arguments.Character))
            {
                var character = command.Get(Constants.Arguments.Character);
                if (character != null)
                {
                    doAction(character);
                    return;
                }
            }

            var characters = command.Get<JsonArray>(Constants.Arguments.MultipleCharacters);
            if (characters != null)
                CharacterManager.Set(characters.Select(x => x as string), ListKind.Ignored);

            // todo: add notification for this
        }

        private void InitialCharacterListCommand(IDictionary<string, object> command)
        {
            var arr = (JsonArray) command[Constants.Arguments.MultipleCharacters];
            foreach (JsonArray character in arr)
            {
                ICharacter temp = new CharacterModel();

                temp.Name = (string) character[0]; // Character's name

                temp.Gender = ParseGender((string) character[1]); // character's gender

                temp.Status = character[2].ToEnum<StatusType>();

                // Character's status
                temp.StatusMessage = (string) character[3]; // Character's status message

                CharacterManager.SignOn(temp);
            }
        }

        private CommandDelegate InterpretCommand(IDictionary<string, object> command)
        {
            ChatModel.LastMessageReceived = DateTimeOffset.Now;

            if (command == null) return null;

            var commandType = command.Get(Constants.Arguments.Command);

            Log(commandType + " " + command.GetHashCode(), noisyTypes.Contains(commandType));

            switch (commandType)
            {
                case Commands.SystemAuthenticate:
                    return LoginCommand;
                case Commands.SystemUptime:
                    return UptimeCommand;
                case Commands.AdminList:
                    return AdminsCommand;
                case Commands.UserIgnore:
                    return IgnoreUserCommand;
                case Commands.UserList:
                    return InitialCharacterListCommand;
                case Commands.PublicChannelList:
                    return PublicChannelListCommand;
                case Commands.PrivateChannelList:
                    return PrivateChannelListCommand;
                case Commands.UserStatus:
                    return StatusChangedCommand;
                case Commands.ChannelAd:
                    return AdMessageCommand;
                case Commands.ChannelMessage:
                    return ChannelMessageCommand;
                case Commands.UserMessage:
                    return PrivateMessageCommand;
                case Commands.UserTyping:
                    return TypingStatusCommand;
                case Commands.ChannelJoin:
                    return JoinChannelCommand;
                case Commands.ChannelLeave:
                    return LeaveChannelCommand;
                case Commands.ChannelModerators:
                    return ChannelOperatorListCommand;
                case Commands.ChannelInitialize:
                    return ChannelInitializedCommand;
                case Commands.ChannelDescription:
                    return ChannelDescriptionCommand;
                case Commands.SystemError:
                case Commands.SystemMessage:
                    return ErrorCommand;
                case Commands.UserInvite:
                    return InviteCommand;
                case Commands.ChannelKick:
                    return KickCommand;
                case Commands.ChannelBan:
                    return KickCommand;
                case Commands.UserJoin:
                    return UserLoggedInCommand;
                case Commands.UserLeave:
                    return CharacterDisconnectCommand;
                case Commands.ChannelRoll:
                    return RollCommand;
                case Commands.AdminDemote:
                    return OperatorDemoteCommand;
                case Commands.AdminPromote:
                    return OperatorPromoteCommand;
                case Commands.ChannelDemote:
                    return OperatorDemoteCommand;
                case Commands.ChannelPromote:
                    return OperatorPromoteCommand;
                case Commands.ChannelMode:
                    return RoomModeChangedCommand;
                case Commands.AdminBroadcast:
                    return BroadcastCommand;
                case Commands.SystemBridge:
                    return RealTimeBridgeCommand;
                case Commands.AdminReport:
                    return NewReportCommand;
                case Commands.SearchResult:
                    return SearchResultCommand;
                case Commands.ChannelSetOwner:
                    return SetNewOwnerCommand;
                default:
                    return null;
            }
        }

        private void SetNewOwnerCommand(IDictionary<string, object> command)
        {
            var character = command.Get(Constants.Arguments.Character);
            var channelId = command.Get(Constants.Arguments.Channel);

            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelId);

            if (channel == null) return;

            var mods = channel.CharacterManager.GetNames(ListKind.Moderator, false).ToList();
            mods[0] = character;
            channel.CharacterManager.Set(mods, ListKind.Moderator);

            var update = new ChannelUpdateModel(channel, new ChannelUpdateModel.ChannelOwnerChangedEventArgs{ NewOwner = character });
            Events.GetEvent<NewUpdateEvent>().Publish(update);
        }

        private void SearchResultCommand(IDictionary<string, object> command)
        {
            var characters = (JsonArray) command[Constants.Arguments.MultipleCharacters];
            CharacterManager.Set(new JsonArray(), ListKind.SearchResult);

            foreach (string character in characters.Where(x => !CharacterManager.IsOnList((string)x, ListKind.NotInterested)))
            {
                CharacterManager.Add(character, ListKind.SearchResult);
            }
            Events.GetEvent<ChatSearchResultEvent>().Publish(null);
            Events.GetEvent<ErrorEvent>().Publish("Got search results successfully.");
        }

        private void InviteCommand(IDictionary<string, object> command)
        {
            var sender = command.Get(Constants.Arguments.Sender);
            var id = command.Get(Constants.Arguments.Name);
            var title = command.Get(Constants.Arguments.Title);

            var args = new ChannelUpdateModel.ChannelInviteEventArgs {Inviter = sender};
            Events.GetEvent<NewUpdateEvent>().Publish(new ChannelUpdateModel(ChatModel.FindChannel(id, title), args));
        }

        private void AutoJoinChannelCommand(IDictionary<string, object> command)
        {
            var title = command.Get(Constants.Arguments.Title);
            var id = command.Get(Constants.Arguments.Channel);

            var characterDict = command.Get<IDictionary<string, object>>(Constants.Arguments.Character);
            var character = characterDict.Get(Constants.Arguments.Identity);

            if (character != ChatModel.CurrentCharacter.Name || !autoJoinedChannels.Contains(id))
            {
                JoinChannelCommand(command);
                return;
            }

            manager.QuickJoinChannel(id, title);
            autoJoinedChannels.Remove(id);
        }

        private void QuickJoinChannelCommand(IDictionary<string, object> command)
        {
            var title = command.Get(Constants.Arguments.Title);
            var id = command.Get(Constants.Arguments.Channel);

            var characterDict = command.Get<IDictionary<string, object>>(Constants.Arguments.Character);
            var character = characterDict.Get(Constants.Arguments.Identity);

            if (character != ChatModel.CurrentCharacter.Name)
            {
                RequeueCommand(command);
                return;
            }

            var kind = ChannelType.Public;
            if (id.Contains("ADH-"))
                kind = ChannelType.Private;

            manager.JoinChannel(kind, id, title);
        }

        new private void JoinChannelCommand(IDictionary<string, object> command)
        {
            var title = command.Get(Constants.Arguments.Title);
            var id = command.Get(Constants.Arguments.Channel);

            var characterDict = command.Get<IDictionary<string, object>>(Constants.Arguments.Character);
            var character = characterDict.Get(Constants.Arguments.Identity);

            // JCH is used in a few situations. It is used when others join a channel and when we join a channel

            // if this is a situation where we are joining a channel...
            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(id);
            if (channel == null)
            {
                var kind = ChannelType.Public;
                if (id.Contains("ADH-"))
                    kind = ChannelType.Private;

                manager.JoinChannel(kind, id, title);
            }
            else
            {
                var toAdd = CharacterManager.Find(character);
                if (!channel.CharacterManager.SignOn(toAdd)) return;

                var update = new CharacterUpdateModel(
                    toAdd,
                    new CharacterUpdateModel.JoinLeaveEventArgs
                        {
                            Joined = true,
                            TargetChannel = channel.Title,
                            TargetChannelId = channel.Id
                        });

                Events.GetEvent<NewUpdateEvent>().Publish(update);
            }
        }

        new private void KickCommand(IDictionary<string, object> command)
        {
            var kicker = command.Get("operator");
            var channelId = command.Get(Constants.Arguments.Channel);
            var kicked = command.Get(Constants.Arguments.Character);
            var isBan = command.Get(Constants.Arguments.Command) == Commands.ChannelBan;
            var channel = ChatModel.FindChannel(channelId) as GeneralChannelModel;

            if (kicked.Equals(ChatModel.CurrentCharacter.Name, StringComparison.OrdinalIgnoreCase))
                kicked = "you";

            var args = new ChannelUpdateModel.ChannelDisciplineEventArgs
                {
                    IsBan = isBan,
                    Kicked = kicked,
                    Kicker = kicker
                };
            var update = new ChannelUpdateModel(channel, args);

            if (kicked == "you")
                manager.RemoveChannel(channelId);
            else
                channel.CharacterManager.SignOff(kicked);

            Events.GetEvent<NewUpdateEvent>().Publish(update);
        }

        private void LeaveChannelCommand(IDictionary<string, object> command)
        {
            var channelId = command.Get(Constants.Arguments.Channel);
            var characterName = command.Get(Constants.Arguments.Character);

            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelId);
            if (ChatModel.CurrentCharacter.NameEquals(characterName))
            {
                if (channel != null)
                    manager.RemoveChannel(channelId, false, true);

                return;
            }

            if (channel == null)
                return;

            var ignoreUpdate = false;

            if (command.ContainsKey("ignoreUpdate"))
                ignoreUpdate = (bool) command["ignoreUpdate"];

            if (channel.CharacterManager.SignOff(characterName) && !ignoreUpdate)
            {
                Events.GetEvent<NewUpdateEvent>().Publish(
                    new CharacterUpdateModel(
                        CharacterManager.Find(characterName),
                        new CharacterUpdateModel.JoinLeaveEventArgs
                            {
                                Joined = false,
                                TargetChannel = channel.Title,
                                TargetChannelId = channel.Id
                            }));
            }
        }

        private void LoginCommand(IDictionary<string, object> command)
        {
            ChatModel.ClientUptime = DateTimeOffset.Now;
            connection.SendMessage(Constants.ClientCommands.SystemUptime);

            Dispatcher.Invoke((Action) delegate { ChatModel.IsAuthenticated = true; });

            // auto join
            var waitTimer = new Timer(200);
            var channels = (from c in ApplicationSettings.SavedChannels
                           where !string.IsNullOrWhiteSpace(c)
                           select new { channel = c })
                           .Distinct()
                           .ToList();

            var walk = channels.GetEnumerator();

            if (walk.MoveNext())
            {
                waitTimer.Elapsed += (s, e) =>
                    {
                        Log("Auto joining " + walk.Current);
                        autoJoinedChannels.Add(walk.Current.channel);
                        connection.SendMessage(walk.Current, Constants.ClientCommands.ChannelJoin);
                        if (walk.MoveNext())
                            return;

                        waitTimer.Stop();
                        waitTimer.Dispose();
                    };
            }

            waitTimer.Start();
        }

        private void MessageReceived(IDictionary<string, object> command, bool isAd)
        {
            var character = command.Get(Constants.Arguments.Character);
            var message = command.Get(Constants.Arguments.Message);
            var channel = command.Get(Constants.Arguments.Channel);

            // dedupe logic
            if (isAd && automation.IsDuplicateAd(character, message))
                return;

            if (!CharacterManager.IsOnList(character, ListKind.Ignored))
                manager.AddMessage(message, channel, character, isAd ? MessageType.Ad : MessageType.Normal);
        }

        private void NewReportCommand(IDictionary<string, object> command)
        {
            var type = command.Get(Constants.Arguments.Action);
            if (string.IsNullOrWhiteSpace(type))
                return;

            if (type.Equals("report"))
            {
                // new report
                var report = command.Get("report");
                var callId = command.Get("callid");
                var logId = command.ContainsKey("logid") ? command["logid"] as int? : null;

                var reportIsClean = false;

                // "report" is in some sort of arbitrary and non-compulsory format
                // attempt to decipher it
                if (report == null) return;

                var rawReport = report.Split('|').Select(x => x.Trim()).ToList();

                var starters = new[] {"Current Tab/Channel:", "Reporting User:", string.Empty};

                // each section should start with one of these
                var reportData = new List<string>();

                for (var i = 0; i < rawReport.Count; i++)
                {
                    if (rawReport[i].StartsWith(starters[i]))
                        reportData.Add(rawReport[i].Substring(starters[i].Length).Trim());
                }

                if (reportData.Count == 3)
                    reportIsClean = true;

                var reporterName = command.Get(Constants.Arguments.Character);
                var reporter = CharacterManager.Find(reporterName);

                if (reportIsClean)
                {
                    Events.GetEvent<NewUpdateEvent>()
                        .Publish(
                            new CharacterUpdateModel(
                                reporter,
                                new CharacterUpdateModel.ReportFiledEventArgs
                                    {
                                        Reported = reportData[0],
                                        Tab = reportData[1],
                                        Complaint = reportData[2],
                                        LogId = logId,
                                        CallId = callId,
                                    }));

                    reporter.LastReport = new ReportModel
                        {
                            Reporter = reporter,
                            Reported = reportData[0],
                            Tab = reportData[1],
                            Complaint = reportData[2],
                            CallId = callId,
                            LogId = logId
                        };
                }
                else
                {
                    Events.GetEvent<NewUpdateEvent>()
                        .Publish(
                            new CharacterUpdateModel(
                                reporter,
                                new CharacterUpdateModel.ReportFiledEventArgs
                                    {
                                        Complaint = report,
                                        CallId = callId,
                                        LogId = logId,
                                    }));

                    reporter.LastReport = new ReportModel
                        {
                            Reporter = reporter,
                            Complaint = report,
                            CallId = callId,
                            LogId = logId
                        };
                }
            }
            else if (type.Equals("confirm"))
            {
                // someone else handling a report
                var handlerName = command.Get("moderator");
                var handled = command.Get(Constants.Arguments.Character);
                var handler = CharacterManager.Find(handlerName);

                Events.GetEvent<NewUpdateEvent>()
                    .Publish(
                        new CharacterUpdateModel(
                            handler, new CharacterUpdateModel.ReportHandledEventArgs {Handled = handled}));
            }
        }

        private void OperatorDemoteCommand(IDictionary<string, object> command)
        {
            var target = command.Get(Constants.Arguments.Character);
            string channelId = null;

            if (command.ContainsKey(Constants.Arguments.Channel))
                channelId = command.Get(Constants.Arguments.Channel);

            PromoteOrDemote(target, false, channelId);
        }

        private void OperatorPromoteCommand(IDictionary<string, object> command)
        {
            var target = command.Get(Constants.Arguments.Character);
            string channelId = null;

            if (command.ContainsKey(Constants.Arguments.Channel))
                channelId = command.Get(Constants.Arguments.Channel);

            PromoteOrDemote(target, true, channelId);
        }

        private void PrivateChannelListCommand(IDictionary<string, object> command)
        {
            ChannelListCommand(command, false);
        }

        private void PrivateMessageCommand(IDictionary<string, object> command)
        {
            var sender = command.Get(Constants.Arguments.Character);
            if (!CharacterManager.IsOnList(sender, ListKind.Ignored))
            {
                if (ChatModel.CurrentPms.FirstByIdOrNull(sender) == null)
                    manager.AddChannel(ChannelType.PrivateMessage, sender);

                manager.AddMessage(command.Get(Constants.Arguments.Message), sender, sender);

                var temp = ChatModel.CurrentPms.FirstByIdOrNull(sender);
                if (temp == null)
                    return;

                temp.TypingStatus = TypingStatus.Clear; // webclient assumption
            }
            else
            {
                connection.SendMessage(
                    new Dictionary<string, object>
                        {
                            {Constants.Arguments.Action, Constants.Arguments.ActionNotify},
                            {Constants.Arguments.Character, sender},
                            {Constants.Arguments.Type, Constants.ClientCommands.UserIgnore}
                        });
            }
        }

        private void PromoteOrDemote(string character, bool isPromote, string channelId = null)
        {
            var target = CharacterManager.Find(character);

            string title = null;
            if (channelId != null)
            {
                var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelId);
                if (channel != null)
                {
                    title = channel.Title;
                    if (isPromote)
                        channel.CharacterManager.Add(character, ListKind.Moderator);
                    else
                        channel.CharacterManager.Remove(character, ListKind.Moderator);
                }
            }


            if (target != null)
            {
                // avoids nasty null reference
                Events.GetEvent<NewUpdateEvent>()
                    .Publish(
                        new CharacterUpdateModel(
                            target,
                            new CharacterUpdateModel.PromoteDemoteEventArgs
                                {
                                    TargetChannelId = channelId,
                                    TargetChannel = title,
                                    IsPromote = isPromote,
                                }));
            }
        }

        private void PublicChannelListCommand(IDictionary<string, object> command)
        {
            ChannelListCommand(command, true);
        }

        private void RealTimeBridgeCommand(IDictionary<string, object> command)
        {
            var type = command.Get(Constants.Arguments.Type);

            if (type == null) return;

            var doListAction = new Action<string, ListKind, bool, bool>((name, listKind, isAdd, giveUpdate) => Dispatcher.Invoke((Action) delegate
                {
                    if (isAdd)
                        CharacterManager.Add(name, listKind);
                    else
                        CharacterManager.Remove(name, listKind);

                    var character = CharacterManager.Find(name);

                    character.IsInteresting = CharacterManager.IsOfInterest(name);

                    var update = new CharacterUpdateModel(
                        character,
                        new CharacterUpdateModel.ListChangedEventArgs
                            {
                                IsAdded = isAdd,
                                ListArgument = listKind
                            });

                    if (giveUpdate)
                        Events.GetEvent<NewUpdateEvent>().Publish(update);
                }));

            if (type.Equals("note"))
            {
                var senderName = command.Get(Constants.Arguments.Sender);
                var subject = command.Get("subject");
                var id = (long) command["id"];

                var update = new CharacterUpdateModel(
                    CharacterManager.Find(senderName),
                    new CharacterUpdateModel.NoteEventArgs
                        {
                            Subject = subject,
                            NoteId = id
                        });

                notes.UpdateNotes(senderName);
                Events.GetEvent<NewUpdateEvent>().Publish(update);
            }
            else if (type.Equals("comment"))
            {
                var name = command.Get(Constants.Arguments.Name);

                // sometimes ID is sent as a string. Sometimes it is sent as a number.
                // so even though it's THE SAME COMMAND we have to treat *each* number differently
                var commentId = long.Parse(command.Get("id"));
                var parentId = (long) command["parent_id"];
                var targetId = long.Parse(command.Get("target_id"));

                var title = HttpUtility.HtmlDecode(command.Get("target"));

                var commentType =
                    command.Get("target_type").ToEnum<CharacterUpdateModel.CommentEventArgs.CommentTypes>();

                var update = new CharacterUpdateModel(
                    CharacterManager.Find(name),
                    new CharacterUpdateModel.CommentEventArgs
                        {
                            CommentId = commentId,
                            CommentType = commentType,
                            ParentId = parentId,
                            TargetId = targetId,
                            Title = title
                        });

                Events.GetEvent<NewUpdateEvent>().Publish(update);
            }
            else if (type.Equals("trackadd"))
            {
                var name = command.Get(Constants.Arguments.Name);
                doListAction(name, ListKind.Bookmark, true, true);
            }
            else if (type.Equals("trackrem"))
            {
                var name = command.Get(Constants.Arguments.Name);
                doListAction(name, ListKind.Bookmark, false, true);
            }
            else if (type.Equals("friendadd"))
            {
                var name = command.Get(Constants.Arguments.Name);
                doListAction(name, ListKind.Friend, true, true);
            }
            else if (type.Equals("friendremove"))
            {
                var name = command.Get(Constants.Arguments.Name);
                doListAction(name, ListKind.Friend, false, false);
            }
        }

        private void RollCommand(IDictionary<string, object> command)
        {
            var channel = command.Get(Constants.Arguments.Channel);
            var message = command.Get(Constants.Arguments.Message);
            var poster = command.Get(Constants.Arguments.Character);

            if (!CharacterManager.IsOnList(poster, ListKind.Ignored))
                manager.AddMessage(message, channel, poster, MessageType.Roll);
        }

        private void RoomModeChangedCommand(IDictionary<string, object> command)
        {
            var channelId = command.Get(Constants.Arguments.Channel);
            var mode = command.Get(Constants.Arguments.Mode);

            var newMode = mode.ToEnum<ChannelMode>();
            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelId);

            if (channel == null)
                return;

            channel.Mode = newMode;
            Events.GetEvent<NewUpdateEvent>()
                .Publish(
                    new ChannelUpdateModel(
                        channel,
                        new ChannelUpdateModel.ChannelModeUpdateEventArgs {NewMode = newMode}));
        }

        private void RoomTypeChangedCommand(IDictionary<string, object> command)
        {
            var channelId = command.Get(Constants.Arguments.Channel);
            var isPublic =
                (command.Get(Constants.Arguments.Message)).IndexOf("public", StringComparison.OrdinalIgnoreCase) !=
                -1;

            var channel = ChatModel.CurrentChannels.FirstByIdOrNull(channelId);

            if (channel == null)
                return; // can't change the settings of a room we don't know

            if (isPublic)
            {
                // room is now open
                channel.Type = ChannelType.Private;

                Events.GetEvent<NewUpdateEvent>()
                    .Publish(
                        new ChannelUpdateModel(
                            channel,
                            new ChannelUpdateModel.ChannelTypeChangedEventArgs {IsOpen = true}));
            }
            else
            {
                // room is InviteOnly
                channel.Type = ChannelType.InviteOnly;

                Events.GetEvent<NewUpdateEvent>()
                    .Publish(
                        new ChannelUpdateModel(
                            channel,
                            new ChannelUpdateModel.ChannelTypeChangedEventArgs {IsOpen = false}));
            }
        }

        private void StatusChangedCommand(IDictionary<string, object> command)
        {
            var target = command.Get(Constants.Arguments.Character);
            var status = command.Get(Constants.Arguments.Status).ToEnum<StatusType>();
            var statusMessage = command.Get(Constants.Arguments.StatusMessage);

            var character = CharacterManager.Find(target);
            var statusChanged = false;
            var statusMessageChanged = false;
            var oldStatus = character.Status;

            if (character.Status != status)
            {
                statusChanged = true;
                character.Status = status;
            }

            if (character.StatusMessage != statusMessage)
            {
                statusMessageChanged = true;
                character.StatusMessage = statusMessage;
            }

            if (!statusChanged && !statusMessageChanged)
                return;

            if (status == StatusType.Idle)
                return;

            if (oldStatus == StatusType.Idle && status == StatusType.Online)
                return;

            var args = new CharacterUpdateModel.StatusChangedEventArgs
                {
                    NewStatusType =
                        statusChanged
                            ? status
                            : StatusType.Offline,
                    NewStatusMessage =
                        statusMessageChanged
                            ? statusMessage
                            : null
                };

            Events.GetEvent<NewUpdateEvent>().Publish(new CharacterUpdateModel(character, args));
        }

        private void TypingStatusCommand(IDictionary<string, object> command)
        {
            var sender = command.Get(Constants.Arguments.Character);

            var channel = ChatModel.CurrentPms.FirstByIdOrNull(sender);
            if (channel == null)
                return;

            var type = command.Get("status").ToEnum<TypingStatus>();

            channel.TypingStatus = type;
        }

        private void UptimeCommand(IDictionary<string, object> command)
        {
            var time = (long) command["starttime"];
            ChatModel.ServerUpTime = HelperConverter.UnixTimeToDateTime(time);
        }

        private void UserLoggedInCommand(IDictionary<string, object> command)
        {
            var character = command.Get(Constants.Arguments.Identity);

            var temp = new CharacterModel
                {
                    Name = character,
                    Gender = ParseGender(command.Get("gender")),
                    Status = command.Get("status").ToEnum<StatusType>()
                };

            CharacterManager.SignOn(temp);

            Events.GetEvent<NewUpdateEvent>()
                .Publish(
                    new CharacterUpdateModel(
                        temp, new CharacterUpdateModel.LoginStateChangedEventArgs {IsLogIn = true}));
        }

        private void GetCharacter(string character)
        {
            ChatModel.CurrentCharacter = new CharacterModel {Name = character, Status = StatusType.Online};
            ChatModel.CurrentCharacter.GetAvatar();

            Dispatcher.Invoke(
                (Action)
                    delegate
                        {
                            Application.Current.MainWindow.Title = string.Format(
                                "{0} {1} ({2})", Constants.ClientId, Constants.ClientName, character);
                        });
        }

        private void WipeState(string message)
        {
            Log("Resetting");

            CharacterManager.Clear();
            ChatModel.CurrentChannels.Each(x => x.CharacterManager.Clear());

            ChatModel.CurrentCharacter.Status = StatusType.Online;
            ChatModel.CurrentCharacter.StatusMessage = string.Empty;

            Dispatcher.Invoke((Action) (() =>
                {
                    ChatModel.AllChannels.Clear();
                    while (ChatModel.CurrentChannels.Count > 1)
                    {
                        ChatModel.CurrentChannels.RemoveAt(1);
                    }

                    ChatModel.CurrentPms.Each(pm => pm.TypingStatus = TypingStatus.Clear);
                }));
        }

        private void RequeueCommand(IDictionary<string, object> command)
        {
            object value;
            if (!command.TryGetValue("retryAttempt", out value))
                value = 0;

            var retryAttempts = (int) value;
            Logging.LogLine(command.Get(Constants.Arguments.Command) 
                + " " + command.GetHashCode() 
                + " fail #" + (retryAttempts + 1), "cmnd serv");

            if (retryAttempts >= 5) return;

            retryAttempts++;
            command["retryAttempt"] = retryAttempts;

            var delay = new Timer(2000 ^ retryAttempts);
            delay.Elapsed += (s, e) =>
            {
                EnqueueAction(command);
                delay.Stop();
                delay.Dispose();
            };
            delay.Start();
        }

        #endregion
    }
}