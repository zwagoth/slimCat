﻿#region Copyright

// <copyright file="PMChannelModel.cs">
//     Copyright (c) 2013-2015, Justin Kadrovach, All rights reserved.
// 
//     This source is subject to the Simplified BSD License.
//     Please see the License.txt file for more information.
//     All other rights reserved.
// 
//     THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
//     KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
//     IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
//     PARTICULAR PURPOSE.
// </copyright>

#endregion

namespace slimCat.Models
{
    #region Usings

    using System.Collections.ObjectModel;
    using System.Text;
    using System.Timers;

    #endregion

    /// <summary>
    ///     Used for Private-Message communication between users
    /// </summary>
    public sealed class PmChannelModel : ChannelModel
    {
        #region Constructors and Destructors

        /// <summary>
        ///     Initializes a new instance of the <see cref="PmChannelModel" /> class.
        ///     Creates a new Channel data model which will always be a Pm
        /// </summary>
        /// <param name="character">
        ///     The character that sent the message
        /// </param>
        public PmChannelModel(ICharacter character)
            : base(character.Name, ChannelType.PrivateMessage)
        {
            TargetCharacter = character;
            TargetCharacter.GetAvatar();

            updateTick = new Timer(1000);
            isTypingString = new StringBuilder();
            Settings = new ChannelSettingsModel(true);

            updateTick.Elapsed += (s, e) =>
            {
                if (isTypingString.Length < 3)
                    isTypingString.Append(".");
                else
                {
                    isTypingString.Clear();
                    isTypingString.Append(".");
                }

                OnPropertyChanged("TypingString");
            };
        }

        #endregion

        #region Fields

        private StringBuilder isTypingString;

        private string noteSubject;

        private ProfileData profileData;

        private ICharacter targetCharacter;

        private TypingStatus typing;

        private Timer updateTick;

        #endregion

        #region Public Properties

        /// <summary>
        ///     Gets the display number.
        /// </summary>
        public int DisplayNumber => Unread;

        public int CompositeUnreadCount => Unread;

        /// <summary>
        ///     Gets or sets the PrivateMessage character.
        /// </summary>
        public ICharacter TargetCharacter
        {
            get { return targetCharacter; }

            set
            {
                targetCharacter = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether is selected.
        /// </summary>
        public override bool IsSelected
        {
            get { return base.IsSelected; }

            set
            {
                base.IsSelected = value;
                if (!value)
                    LastNoteCount = Notes.Count;
            }
        }

        /// <summary>
        ///     Gets or sets the typing status.
        /// </summary>
        public TypingStatus TypingStatus
        {
            get { return typing; }

            set
            {
                typing = value;
                OnPropertyChanged();
                OnPropertyChanged("TypingString");

                updateTick.Enabled = value == TypingStatus.Typing;
            }
        }

        /// <summary>
        ///     Gets the typing string.
        /// </summary>
        public string TypingString
        {
            get
            {
                switch (TypingStatus)
                {
                    case TypingStatus.Paused:
                        return "~";
                    case TypingStatus.Clear:
                        return string.Empty;
                    case TypingStatus.Typing:
                        return isTypingString.ToString();
                }

                return string.Empty;
            }
        }

        public int LastNoteCount { get; set; }

        public bool HasAutoRepliedTo { get; set; }

        public string NoteSubject
        {
            get { return noteSubject; }
            set
            {
                noteSubject = value;
                OnPropertyChanged();
            }
        }

        public ProfileData ProfileData
        {
            get { return profileData; }
            set
            {
                profileData = value;
                OnPropertyChanged();
            }
        }

        public bool ShouldViewNotes { get; set; }

        public bool ShouldViewProfile { get; set; }

        public ObservableCollection<IMessage> Notes { get; } = new ObservableCollection<IMessage>();

        #endregion

        #region Methods

        protected override void Dispose(bool isManaged)
        {
            if (isManaged)
            {
                updateTick.Dispose();
                updateTick = null;
                isTypingString = null;
                Settings = new ChannelSettingsModel(true);
            }

            base.Dispose(isManaged);
        }

        protected override void UpdateBindings()
        {
            base.UpdateBindings();
            OnPropertyChanged("CompositeUnreadCount");
        }

        #endregion
    }
}