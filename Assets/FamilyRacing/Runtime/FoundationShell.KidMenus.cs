using System;
using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Kid-first menu skin for the player presentation: big vector icons on
    // flat, single-colour tiles, one short label each, no borders, no
    // dividers and no stacked panel shades. Element names and command
    // routing stay exactly as the builders and tests expect; this layer
    // only decides how the menus look and how much text they show.
    public sealed partial class FoundationShell
    {
        private static readonly Color KidBg = new Color(0.086f, 0.141f, 0.259f, 1f);
        private static readonly Color KidSurface = new Color(0.149f, 0.227f, 0.384f, 1f);
        private static readonly Color KidText = new Color(1f, 0.98f, 0.94f, 1f);
        private static readonly Color KidMuted = new Color(0.69f, 0.76f, 0.88f, 1f);
        private static readonly Color KidOrange = new Color(1f, 0.55f, 0.24f, 1f);
        private static readonly Color KidTeal = new Color(0.15f, 0.74f, 0.68f, 1f);
        private static readonly Color KidPurple = new Color(0.55f, 0.43f, 0.98f, 1f);
        private static readonly Color KidBlue = new Color(0.22f, 0.58f, 0.98f, 1f);
        private static readonly Color KidYellow = new Color(1f, 0.79f, 0.22f, 1f);
        private static readonly Color KidPink = new Color(0.98f, 0.42f, 0.62f, 1f);
        private static readonly Color KidGreen = new Color(0.20f, 0.78f, 0.45f, 1f);
        private static readonly Color KidInk = new Color(0.10f, 0.12f, 0.20f, 1f);
        private static readonly Color[] KidCardColors = { KidOrange, KidTeal, KidPurple, KidPink };

        private bool Rtl(FoundationState state) => state != null && state.language == "he";

        // --- building blocks ------------------------------------------------

        private bool RootRtl => root != null && root.languageDirection == LanguageDirection.RTL;

        // Pin a mark to the reading-start side of a button and pad the text
        // away from it. The Button keeps its own text, so its name, text and
        // callback stay exactly what the builders and tests expect.
        private void SideMark(Button button, VisualElement mark, float width)
        {
            if (button == null || mark == null) return;
            if (button.parent != null && button.parent.name == "kid.wrap") return;
            var parent = button.parent;
            if (parent == null) return;
            // A Button that owns children stops measuring its own text, so
            // the mark lives in a wrapper beside the (still leaf) Button.
            var wrap = new VisualElement { name = "kid.wrap" };
            wrap.style.flexShrink = 0;
            wrap.style.alignSelf = button.style.alignSelf;
            parent.Insert(parent.IndexOf(button), wrap);
            wrap.Add(button);
            var holder = new VisualElement { name = "kid.mark", pickingMode = PickingMode.Ignore };
            holder.style.position = Position.Absolute;
            holder.style.flexDirection = FlexDirection.Row;
            holder.style.alignItems = Align.Center;
            holder.style.justifyContent = Justify.Center;
            holder.Add(mark);
            wrap.Add(holder);
            const float gap = 16f;
            button.style.whiteSpace = WhiteSpace.NoWrap;
            button.style.flexShrink = 0;
            var rtl = RootRtl;
            if (rtl) button.style.paddingRight = width + gap + 10;
            else button.style.paddingLeft = width + gap + 10;
            button.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var box = button.layout;
                holder.style.top = box.y;
                holder.style.height = box.height;
                holder.style.left = rtl ? box.xMax - gap - width : box.x + gap;
                holder.style.width = width;
            });
        }

        private static void Pad(VisualElement e, float v, float h)
        {
            e.style.paddingTop = e.style.paddingBottom = v;
            e.style.paddingLeft = e.style.paddingRight = h;
        }

        private static void Margin(VisualElement e, float v, float h)
        {
            e.style.marginTop = e.style.marginBottom = v;
            e.style.marginLeft = e.style.marginRight = h;
        }

        private static void NoBorder(VisualElement e)
        {
            e.style.borderTopWidth = e.style.borderBottomWidth =
                e.style.borderLeftWidth = e.style.borderRightWidth = 0;
        }

        // A soft squash on press: the one piece of motion every tile shares,
        // so a tap always answers even before the screen changes.
        private static void AddPressFeedback(VisualElement e)
        {
            VisualElement Target() => e.parent != null && e.parent.name == "kid.wrap" ? e.parent : e;
            e.RegisterCallback<PointerDownEvent>(_ =>
                Target().style.scale = new Scale(new Vector3(0.95f, 0.95f, 1f)), TrickleDown.TrickleDown);
            EventCallback<EventBase> reset = _ => Target().style.scale = new Scale(Vector3.one);
            e.RegisterCallback<PointerUpEvent>(evt => reset(evt), TrickleDown.TrickleDown);
            e.RegisterCallback<PointerLeaveEvent>(evt => reset(evt));
            e.RegisterCallback<PointerCancelEvent>(evt => reset(evt), TrickleDown.TrickleDown);
        }

        private static Color IconInk(Color fill) =>
            fill == KidYellow ? KidInk : KidText;

        // Big square tile: icon on top, one short word underneath.
        private Button KidTile(VisualElement parent, string icon, string text, Color fill,
            Action onClick, string name, float width, float height, float iconSize, int fontSize,
            FoundationState state)
        {
            var button = new Button(onClick) { text = "", name = name };
            button.style.width = width;
            button.style.height = height;
            button.style.backgroundColor = fill;
            button.style.flexDirection = FlexDirection.Column;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            NoBorder(button);
            Round(button, 28);
            Margin(button, 8, 8);
            Pad(button, 10, 10);
            button.focusable = true;
            var ink = IconInk(fill);
            button.Add(new MenuIcon(icon, iconSize, ink, MirrorIcon(icon, state)));
            if (!string.IsNullOrEmpty(text))
            {
                var label = new Label(text) { name = name + ".label", pickingMode = PickingMode.Ignore };
                label.style.fontSize = fontSize;
                label.style.color = ink;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.marginTop = 6;
                button.Add(label);
            }
            AddPressFeedback(button);
            parent.Add(button);
            return button;
        }

        // Round icon-only button for utilities (back, settings, exit).
        private Button KidRoundButton(VisualElement parent, string icon, Color fill, Action onClick,
            string name, float size, FoundationState state, string tooltip = null)
        {
            var button = new Button(onClick) { text = "", name = name };
            button.style.width = button.style.height = size;
            button.style.minWidth = size;
            button.style.backgroundColor = fill;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            NoBorder(button);
            Round(button, size / 2f);
            Margin(button, 0, 6);
            Pad(button, 0, 0);
            button.focusable = true;
            if (!string.IsNullOrEmpty(tooltip)) button.tooltip = tooltip;
            button.Add(new MenuIcon(icon, size * 0.5f, IconInk(fill), MirrorIcon(icon, state)));
            AddPressFeedback(button);
            parent.Add(button);
            return button;
        }

        private bool MirrorIcon(string icon, FoundationState state) =>
            Rtl(state) && (icon == "back" || icon == "exit" || icon == "next");

        // Turn an existing builder button into an icon + short-label chip
        // without changing its name, callback or text (tests read text).
        private void KidChip(Button button, string icon, bool selected, float iconSize = 34)
        {
            if (button == null) return;
            NoBorder(button);
            Round(button, 22);
            button.style.backgroundColor = selected ? KidOrange : KidSurface;
            button.style.color = KidText;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            Pad(button, 8, 18);
            if (!string.IsNullOrEmpty(icon))
                SideMark(button, new MenuIcon(icon, iconSize, KidText), iconSize);
        }

        // Builder buttons carry their text in the Button itself; wrap that
        // text after an icon by moving it into a label child.
        private void IconBeforeText(Button button, string icon, float iconSize, FoundationState state,
            Color? ink = null)
        {
            if (button == null) return;
            button.style.color = ink ?? KidText;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            SideMark(button, new MenuIcon(icon, iconSize, ink ?? KidText, MirrorIcon(icon, state)), iconSize);
        }

        // The single primary action on a setup screen: a big green "go".
        private void KidStartButton(Button start, FoundationState state)
        {
            if (start == null) return;
            NoBorder(start);
            Round(start, 34);
            start.style.backgroundColor = KidGreen;
            start.style.minWidth = 300;
            start.style.height = 84;
            start.style.minHeight = 84;
            start.style.fontSize = 32;
            start.style.alignSelf = Align.Center;
            Margin(start, 10, 8);
            IconBeforeText(start, "play", 44, state);
            AddPressFeedback(start);
        }

        // Back moves out of the scrolling content to a fixed corner so it
        // is always in the same place on every screen.
        private void KidCornerBack(Button back, FoundationState state)
        {
            if (back == null) return;
            var text = back.text;
            back.text = "";
            back.tooltip = text;
            back.Clear();
            back.style.position = Position.Absolute;
            back.style.top = 14;
            if (Rtl(state)) { back.style.right = 14; back.style.left = StyleKeyword.Auto; }
            else { back.style.left = 14; back.style.right = StyleKeyword.Auto; }
            back.style.width = back.style.height = 72;
            back.style.minWidth = back.style.minHeight = 72;
            back.style.backgroundColor = KidSurface;
            NoBorder(back);
            Round(back, 36);
            Margin(back, 0, 0);
            Pad(back, 0, 0);
            back.style.alignItems = Align.Center;
            back.style.justifyContent = Justify.Center;
            back.Add(new MenuIcon("back", 38, KidText, Rtl(state)));
            AddPressFeedback(back);
            root.Add(back);
        }

        private Label KidTitle(VisualElement parent, string text, int size = 34)
        {
            var title = AddLabel(parent, text, size, KidText);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginTop = 6;
            title.style.marginBottom = 10;
            return title;
        }

        private VisualElement KidImage(Texture2D texture, float width, float height, float radius,
            BackgroundSizeType sizeType = BackgroundSizeType.Cover)
        {
            var image = new VisualElement { pickingMode = PickingMode.Ignore };
            image.style.width = width;
            image.style.height = height;
            image.style.flexShrink = 0;
            Round(image, radius);
            if (texture != null)
            {
                image.style.backgroundImage = new StyleBackground(texture);
                image.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                image.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                image.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                image.style.backgroundSize = new BackgroundSize(sizeType);
            }
            return image;
        }

        // The course art the home tiles use: the first authored courses of
        // the running content, whatever their ids are in this build.
        private Texture2D HomeCourseArt(int index)
        {
            if (playerHud == null) return null;
            var ids = playerHud.courseIds;
            if (ids != null && ids.Length > 0)
                return playerHud.CoursePreview(ids[Mathf.Clamp(index, 0, ids.Length - 1)]);
            return playerHud.CoursePreview(index == 0 ? "coastal-village" : "pine-forest");
        }

        private VisualElement CoinPill(VisualElement parent, int coins, string name)
        {
            var pill = new VisualElement { name = name + ".pill" };
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.backgroundColor = KidSurface;
            Round(pill, 26);
            Pad(pill, 6, 16);
            Margin(pill, 0, 6);
            pill.Add(new MenuIcon("coin", 34, KidYellow));
            var label = new Label(Ltr(coins.ToString())) { name = name };
            label.style.fontSize = 26;
            label.style.color = KidText;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginLeft = 8;
            pill.Add(label);
            parent.Add(pill);
            return pill;
        }

        // --- profiles: "who's racing?" --------------------------------------

        private void BuildKidProfiles(FoundationState state)
        {
            var top = new VisualElement { name = "profiles.top" };
            top.style.flexDirection = FlexDirection.Row;
            top.style.justifyContent = Justify.SpaceBetween;
            top.style.alignItems = Align.Center;
            top.style.height = 72;
            root.Add(top);

            var lang = new VisualElement { name = "profiles.language" };
            lang.style.flexDirection = FlexDirection.Row;
            lang.style.alignItems = Align.Center;
            lang.style.backgroundColor = KidSurface;
            Round(lang, 32);
            Pad(lang, 4, 4);
            lang.Add(new MenuIcon("globe", 34, KidMuted) { style = { marginLeft = 8, marginRight = 4 } });
            langEnButton = AddButton(lang, "EN", PlayerSlate, () => Language("en"), "settings.language.en");
            langHeButton = AddButton(lang, "עב", PlayerSlate, () => Language("he"), "settings.language.he");
            foreach (var b in new[] { langEnButton, langHeButton })
            {
                b.style.minWidth = 70; b.style.height = 54; b.style.minHeight = 54;
                b.style.fontSize = 22; Margin(b, 0, 2); Round(b, 27);
            }
            HighlightPlayerToggle(langEnButton, state.language == "en");
            HighlightPlayerToggle(langHeButton, state.language == "he");
            ApplyAvailability(langEnButton, "ui.language");
            ApplyAvailability(langHeButton, "ui.language");
            top.Add(lang);

            var title = KidTitle(top, T("home.who", state), 40);
            title.name = "profile.select";
            title.style.flexGrow = 1;

            KidRoundButton(top, "exit", KidSurface, () => Application.Quit(), "action.exitGame", 72, state,
                T("action.exitGame", state));

            // Kept for tests and the validation bridge, but never shown:
            // kids pick a face, not a product name.
            var hiddenTitle = AddLabel(root, T("home.title", state), 1, KidText);
            hiddenTitle.name = "player.title";
            hiddenTitle.style.display = DisplayStyle.None;
            var disclaimer = AddLabel(root, T(LoopContentAvailable()
                ? "shell.disclaimer" : "shell.disclaimer.unavailable", state), 1, KidMuted);
            disclaimer.name = "shell.disclaimer";
            disclaimer.style.display = DisplayStyle.None;

            var cards = Row(root);
            cards.name = "profile.cards";
            cards.style.flexGrow = 1;
            cards.style.alignItems = Align.Center;
            cards.style.alignContent = Align.Center;
            if (Rtl(state))
            {
                cards.style.flexDirection = FlexDirection.RowReverse;
                top.style.flexDirection = FlexDirection.RowReverse;
            }
            for (var i = 0; i < state.slots.Count; i++)
            {
                var slot = i;
                var info = state.slots[slot];
                var color = KidCardColors[slot % KidCardColors.Length];
                var card = new VisualElement { name = "profile." + slot };
                Margin(card, 10, 10);
                var button = new Button(() => SelectPlayerCard(state, slot))
                    { name = "profile." + slot + ".select", text = "" };
                button.style.width = 270;
                button.style.height = 380;
                button.style.backgroundColor = color;
                button.style.alignItems = Align.Center;
                button.style.justifyContent = Justify.FlexStart;
                NoBorder(button);
                Round(button, 34);
                Pad(button, 18, 14);
                Margin(button, 0, 0);
                button.focusable = true;

                var portrait = playerHud != null ? playerHud.Portrait(slot) : null;
                var face = KidImage(portrait, 220, 220, 110, BackgroundSizeType.Cover);
                face.name = "profile." + slot + ".portrait";
                face.style.backgroundColor = new Color(1f, 1f, 1f, 0.22f);
                button.Add(face);

                var animal = new Label(T(AnimalKeys[slot % AnimalKeys.Length], state))
                    { pickingMode = PickingMode.Ignore };
                animal.style.fontSize = 34;
                animal.style.color = KidText;
                animal.style.unityFontStyleAndWeight = FontStyle.Bold;
                animal.style.marginTop = 14;
                button.Add(animal);

                var status = new VisualElement { pickingMode = PickingMode.Ignore };
                status.style.flexDirection = FlexDirection.Row;
                status.style.alignItems = Align.Center;
                status.style.marginTop = 8;
                var statusText = new Label { name = "profile." + slot + ".status", pickingMode = PickingMode.Ignore };
                statusText.style.fontSize = 24;
                statusText.style.color = KidText;
                statusText.style.unityFontStyleAndWeight = FontStyle.Bold;
                if (info.status == "Missing")
                {
                    status.Add(new MenuIcon("plus", 34, KidText));
                    statusText.text = T("player.profile.new", state);
                    statusText.style.marginLeft = 6;
                }
                else if (info.status == "Blocked")
                {
                    status.Add(new MenuIcon("lock", 30, KidText));
                    statusText.text = T("status.blocked", state);
                }
                else if (info.status == "RecoveredFromBackup")
                    statusText.text = T("status.recovered", state);
                else
                {
                    status.Add(new MenuIcon("coin", 32, KidYellow));
                    statusText.text = Ltr((info.profile != null ? info.profile.coins : 0).ToString());
                    statusText.style.marginLeft = 6;
                }
                statusText.style.whiteSpace = WhiteSpace.Normal;
                status.Add(statusText);
                button.Add(status);

                ApplyAvailability(button, info.status == "Missing" ? "profile.create" : "profile.select");
                if (info.status == "Blocked") button.SetEnabled(false);
                AddPressFeedback(button);
                card.Add(button);
                cards.Add(card);
            }

            warningLabel = AddLabel(root, "", 16, Coral);
            warningLabel.name = "status.fontWarning";
            warningLabel.style.whiteSpace = WhiteSpace.Normal;
            statusLabel = AddLabel(root, "", 16, KidText);
            statusLabel.name = "status.persistence";
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
        }

        // --- home: one tap to every mode -------------------------------------

        private void BuildKidHome(FoundationState state)
        {
            var scroll = new ScrollView { name = "home.scroll" };
            scroll.style.flexGrow = 1;
            scroll.contentContainer.style.alignItems = Align.Center;
            root.Add(scroll);

            var header = new VisualElement { name = "home.header" };
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignSelf = Align.Stretch;
            header.style.height = 84;
            header.style.marginBottom = 6;
            scroll.Add(header);

            var activeSlot = state.activeSlot >= 0 ? state.activeSlot : 0;
            var driver = new Button(() => app.Dispatch("ui.navigate", Screen("profiles")))
                { name = "action.back", text = "" };
            driver.tooltip = T("player.switchDriver", state);
            driver.style.flexDirection = FlexDirection.Row;
            driver.style.alignItems = Align.Center;
            driver.style.backgroundColor = KidSurface;
            driver.style.height = 76;
            NoBorder(driver);
            Round(driver, 38);
            Pad(driver, 4, 4);
            Margin(driver, 0, 0);
            var avatar = KidImage(playerHud != null ? playerHud.Portrait(activeSlot) : null, 68, 68, 34);
            avatar.name = "home.driver.portrait";
            avatar.style.backgroundColor = KidCardColors[activeSlot % KidCardColors.Length];
            driver.Add(avatar);
            var driverName = new Label(T(AnimalKeys[activeSlot % AnimalKeys.Length], state))
                { name = "home.driver.name", pickingMode = PickingMode.Ignore };
            driverName.style.fontSize = 26;
            driverName.style.color = KidText;
            driverName.style.unityFontStyleAndWeight = FontStyle.Bold;
            driverName.style.marginLeft = driverName.style.marginRight = 12;
            driver.Add(driverName);
            driver.Add(new MenuIcon("swap", 30, KidMuted) { style = { marginRight = 14, marginLeft = 2 } });
            ApplyAvailability(driver, "ui.navigate");
            AddPressFeedback(driver);
            var driverBox = new VisualElement { name = "home.driver" };
            driverBox.Add(driver);
            header.Add(driverBox);

            // The product name stays in the tree for tests and screen
            // readers; the header shows only what a child acts on.
            var titleBlock = new VisualElement { name = "home.titleBlock" };
            titleBlock.style.display = DisplayStyle.None;
            AddLabel(titleBlock, T("home.title", state), 1, KidText).name = "home.title";
            AddLabel(titleBlock, T("home.subtitle", state), 1, KidText).name = "home.subtitle";
            header.Add(titleBlock);

            var right = new VisualElement();
            right.style.flexDirection = FlexDirection.Row;
            right.style.alignItems = Align.Center;
            var coins = state.savedProfile != null ? state.savedProfile.coins : 0;
            CoinPill(right, coins, "home.coins");
            var settings = KidRoundButton(right, "gear", KidSurface,
                () => app.Dispatch("ui.navigate", Screen("settings")), "action.settings", 72, state,
                T("action.settings", state));
            ApplyAvailability(settings, "ui.navigate");
            KidRoundButton(right, "exit", KidSurface, () => Application.Quit(), "action.exitGame", 72, state,
                T("action.exitGame", state));
            header.Add(right);
            // Reading order: in Hebrew the driver sits on the right and the
            // first mode tile is the right-most one.
            if (Rtl(state)) header.style.flexDirection = FlexDirection.RowReverse;

            // Two hero tiles: race right now, or the adventure map.
            var hero = Row(scroll);
            hero.name = "home.hero";
            var quickCard = new VisualElement { name = "home.quickCard" };
            var quick = HeroTile(quickCard, "flag", T("quick.title", state), KidOrange, HomeCourseArt(0),
                () => app.Dispatch("quickRace.open", emptyParams), "loop.quickRace", state);
            ApplyAvailability(quick, "quickRace.open");
            hero.Add(quickCard);
            var campaignCard = new VisualElement { name = "home.campaignCard" };
            var campaign = HeroTile(campaignCard, "map", T("home.tile.campaign", state), KidTeal, HomeCourseArt(1),
                () => app.Dispatch("campaign.open", emptyParams), "loop.campaign", state);
            var prog = state.progression;
            if (prog != null && prog.available)
            {
                var stars = new VisualElement { name = "home.campaign.stars", pickingMode = PickingMode.Ignore };
                stars.style.flexDirection = FlexDirection.Row;
                stars.style.alignItems = Align.Center;
                stars.style.position = Position.Absolute;
                stars.style.top = 14;
                stars.style.right = 14;
                stars.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
                Round(stars, 20);
                Pad(stars, 4, 12);
                stars.Add(new MenuIcon("star", 28, KidYellow));
                var count = new Label(Ltr(prog.campaignStars + "/" + prog.campaignRequiredStars))
                    { pickingMode = PickingMode.Ignore };
                count.style.fontSize = 22;
                count.style.color = KidText;
                count.style.unityFontStyleAndWeight = FontStyle.Bold;
                count.style.marginLeft = 6;
                stars.Add(count);
                campaign.Add(stars);
            }
            ApplyAvailability(campaign, "campaign.open");
            hero.Add(campaignCard);

            // Five more modes as big square icon tiles.
            var loop = Row(scroll);
            loop.name = "loop.entries";
            loop.style.marginTop = 4;
            const float w = 226, h = 222, icon = 96;
            var trial = KidTile(loop, "stopwatch", T("home.tile.trial", state), KidPurple,
                () => app.Dispatch("timeTrial.open", emptyParams), "loop.timeTrial", w, h, icon, 24, state);
            var garage = KidTile(loop, "car", T("garage.title", state), KidBlue,
                () => app.Dispatch("garage.open", emptyParams), "loop.garage", w, h, icon, 24, state);
            var bounties = KidTile(loop, "star", T("bounties.title", state), KidYellow,
                () => app.Dispatch("bounties.open", emptyParams), "loop.bounties", w, h, icon, 24, state);
            var practice = KidTile(loop, "wheel", T("home.tile.practice", state), KidGreen,
                () => app.Dispatch("ui.navigate", Screen("practice")), "action.practice", w, h, icon, 24, state);
            var lan = KidTile(loop, "friends", T("home.tile.lan", state), KidPink,
                () => app.Dispatch("ui.navigate", Screen("lan-browser")), "action.lan", w, h, icon, 24, state);
            if (Rtl(state))
            {
                hero.style.flexDirection = FlexDirection.RowReverse;
                loop.style.flexDirection = FlexDirection.RowReverse;
            }
            ApplyAvailability(trial, "timeTrial.open");
            ApplyAvailability(garage, "garage.open");
            ApplyAvailability(bounties, "bounties.open");
            ApplyAvailability(practice, "race.start");
            ApplyAvailability(lan, "ui.navigate");

            warningLabel = AddLabel(root, "", 16, Coral);
            warningLabel.name = "status.fontWarning";
            warningLabel.style.whiteSpace = WhiteSpace.Normal;
            statusLabel = AddLabel(root, "", 16, KidText);
            statusLabel.name = "status.persistence";
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
        }

        // Wide tile: course art fills the top, a solid band carries the
        // icon and the one word.
        private Button HeroTile(VisualElement parent, string icon, string text, Color fill, Texture2D art,
            Action onClick, string name, FoundationState state)
        {
            var button = new Button(onClick) { name = name, text = "" };
            button.style.width = 596;
            button.style.height = 318;
            button.style.backgroundColor = fill;
            button.style.flexDirection = FlexDirection.Column;
            button.style.overflow = Overflow.Hidden;
            NoBorder(button);
            Round(button, 34);
            Pad(button, 0, 0);
            Margin(button, 8, 10);
            button.focusable = true;
            var image = KidImage(art, 596, 210, 0);
            image.style.width = new Length(100, LengthUnit.Percent);
            image.style.backgroundColor = Color.Lerp(fill, KidBg, 0.35f);
            button.Add(image);
            var band = new VisualElement { pickingMode = PickingMode.Ignore };
            band.style.flexGrow = 1;
            band.style.flexDirection = FlexDirection.Row;
            band.style.alignItems = Align.Center;
            band.style.justifyContent = Justify.Center;
            band.Add(new MenuIcon(icon, 64, KidText, MirrorIcon(icon, state)));
            var label = new Label(text) { name = name + ".label", pickingMode = PickingMode.Ignore };
            label.style.fontSize = 38;
            label.style.color = KidText;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginLeft = 16;
            band.Add(label);
            button.Add(band);
            AddPressFeedback(button);
            parent.Add(button);
            return button;
        }

        // --- per-screen skin passes -----------------------------------------

        private void StyleKidScreen(FoundationState state)
        {
            switch (state.screen)
            {
                case FoundationService.ScreenQuickRace: StyleKidPicker(state, "quick"); break;
                case FoundationService.ScreenTimeTrial:
                    StyleKidPicker(state, "trial");
                    StyleKidTrialExtras(state);
                    break;
                case FoundationService.ScreenCampaign: StyleKidCampaign(state); break;
                case FoundationService.ScreenBounties: StyleKidBounties(state); break;
                case "settings": StyleKidSettings(state); break;
                case "practice": StyleKidPractice(state); break;
                case "lan-browser":
                case "lan-lobby":
                case "lan-results":
                case "lan-recovery": StyleKidLan(state); break;
            }
            foreach (var scroller in root.Query<ScrollView>().ToList())
            {
                scroller.verticalScrollerVisibility = ScrollerVisibility.Hidden;
                scroller.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }
            if (Rtl(state))
                foreach (var row in root.Query<VisualElement>().ToList())
                {
                    var n = row.name ?? "";
                    if (n.StartsWith("quick.courses.") || n.StartsWith("trial.courses.")
                        || n.EndsWith(".pickRow") || n.EndsWith(".optionRow")
                        || n == "quick.classes" || n == "trial.classes" || n == "quick.vehicles"
                        || n == "quick.difficulties" || n == "quick.modes" || n == "campaign.grid"
                        || n == "campaign.setup" || n == "campaign.vehicles" || n == "campaign.difficulties")
                        row.style.flexDirection = FlexDirection.RowReverse;
                }
            if (state.screen == "sandbox" || state.screen == "profiles") return;
            var back = root.Q<Button>("loop.back") ?? root.Q<Button>("action.back");
            if (back != null)
            {
                var oldRow = back.parent;
                KidCornerBack(back, state);
                if (oldRow != null && oldRow != root && oldRow.childCount == 0)
                    oldRow.style.display = DisplayStyle.None;
            }
            // Titles stay clear of the corner back button.
            root.style.paddingTop = 16;
        }

        private void StyleKidPractice(FoundationState state)
        {
            var scroll = root.Q<ScrollView>("practice.scroll");
            if (scroll != null && scroll.contentContainer.childCount > 1)
            {
                var title = scroll.contentContainer[0] as Label;
                if (title != null)
                {
                    title.style.fontSize = 34;
                    title.style.unityFontStyleAndWeight = FontStyle.Bold;
                    title.style.color = KidText;
                    title.style.marginTop = 10;
                }
                if (scroll.contentContainer[1] is Label hint) hint.style.display = DisplayStyle.None;
            }
            foreach (var pair in new[] { ("practice.car", "car", KidOrange), ("practice.motorcycle", "motorcycle", KidTeal) })
            {
                var button = root.Q<Button>(pair.Item1);
                if (button == null) continue;
                NoBorder(button);
                Round(button, 30);
                button.style.backgroundColor = pair.Item3;
                button.style.width = 300;
                button.style.height = 150;
                button.style.fontSize = 28;
                var text = button.text;
                button.text = "";
                button.style.flexDirection = FlexDirection.Column;
                button.style.alignItems = Align.Center;
                button.style.justifyContent = Justify.Center;
                button.Add(new MenuIcon(pair.Item2, 80, KidText));
                var label = new Label(text) { name = pair.Item1 + ".label", pickingMode = PickingMode.Ignore };
                label.style.fontSize = 26;
                label.style.color = KidText;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                button.Add(label);
                Margin(button, 8, 10);
            }
            StyleBigChip(root.Q<Button>("tutorial.car"), "car", false, 180, 64, 36);
            StyleBigChip(root.Q<Button>("tutorial.motorcycle"), "motorcycle", false, 180, 64, 36);
            var keys = root.Q<Label>("practice.keys");
            if (keys != null && UseTouchControls) keys.style.display = DisplayStyle.None;
            foreach (var label in root.Query<Label>().ToList())
                if (ColorDistance(label.style.color.value, Deep) < 0.12f)
                    label.style.color = KidMuted;
        }

        private static string VehicleIcon(string id) =>
            id != null && id.StartsWith("motorcycle") ? "motorcycle" : "car";

        private static int DifficultyRank(string id)
        {
            switch (id)
            {
                case "easy": return 1;
                case "normal": case "medium": return 2;
                case "hard": return 3;
                default: return 2;
            }
        }

        private static string EventIcon(string eventType, bool finale)
        {
            if (finale || eventType == "finale") return "trophy";
            switch (eventType)
            {
                case "time": return "stopwatch";
                case "boost": return "bolt";
                case "stunt": return "star";
                case "gadget": return "bolt";
                case "clean-driving": return "check";
                default: return "flag";
            }
        }

        private void HideLabel(string name)
        {
            var label = root.Q(name);
            if (label != null) label.style.display = DisplayStyle.None;
        }

        // Hide caption labels that only explain an icon row ("Difficulty",
        // "Race type"): the icons carry the meaning.
        private static void HideRowCaptions(VisualElement row)
        {
            if (row == null) return;
            foreach (var child in row.Children())
                if (child is Label) child.style.display = DisplayStyle.None;
        }

        private void KidStars(VisualElement parent, int stars, float size)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            for (var i = 0; i < 3; i++)
                row.Add(new MenuIcon("star", size, i < stars ? KidYellow : new Color(1f, 1f, 1f, 0.28f)));
            parent.Add(row);
        }

        private void StyleKidPicker(FoundationState state, string prefix)
        {
            var title = root.Q<Label>(prefix + ".title");
            if (title != null)
            {
                title.style.fontSize = 34;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = KidText;
                title.style.marginBottom = 4;
                title.style.marginTop = 10;
                var scroll = title.parent;
                if (scroll != null)
                {
                    var index = scroll.IndexOf(title);
                    // The hint line right under the title is prose; drop it.
                    if (index + 1 < scroll.childCount && scroll[index + 1] is Label hint
                        && string.IsNullOrEmpty(hint.name))
                        hint.style.display = DisplayStyle.None;
                }
            }
            var worlds = 0;
            foreach (var label in root.Query<Label>().ToList())
                if (label.name != null && label.name.StartsWith(prefix + ".world.")) worlds++;
            if (worlds <= 1)
                foreach (var label in root.Query<Label>().ToList())
                    if (label.name != null && label.name.StartsWith(prefix + ".world."))
                        label.style.display = DisplayStyle.None;

            var selectedCourse = prefix == "quick" ? quickCourseId : trialCourseId;
            var selectedClass = prefix == "quick" ? quickClassId : trialClassId;
            foreach (var button in root.Query<Button>().ToList())
            {
                var n = button.name ?? "";
                if (n.StartsWith(prefix + ".course."))
                {
                    var courseId = n.Substring((prefix + ".course.").Length);
                    var selected = courseId == selectedCourse;
                    var text = button.text;
                    button.text = "";
                    button.Clear();
                    button.style.width = 320;
                    button.style.height = 236;
                    button.style.flexDirection = FlexDirection.Column;
                    button.style.backgroundColor = selected ? KidOrange : KidSurface;
                    button.style.overflow = Overflow.Hidden;
                    NoBorder(button);
                    Round(button, 28);
                    Pad(button, 0, 0);
                    Margin(button, 8, 8);
                    var art = KidImage(playerHud != null ? playerHud.CoursePreview(courseId) : null, 320, 170, 0);
                    art.style.width = new Length(100, LengthUnit.Percent);
                    art.style.backgroundColor = KidBg;
                    button.Add(art);
                    var label = new Label(text) { name = n + ".label", pickingMode = PickingMode.Ignore };
                    label.style.fontSize = 24;
                    label.style.color = KidText;
                    label.style.unityFontStyleAndWeight = FontStyle.Bold;
                    label.style.unityTextAlign = TextAnchor.MiddleCenter;
                    label.style.flexGrow = 1;
                    button.Add(label);
                    if (selected)
                    {
                        var tick = new VisualElement { pickingMode = PickingMode.Ignore };
                        tick.style.position = Position.Absolute;
                        tick.style.top = 10; tick.style.right = 10;
                        tick.style.width = tick.style.height = 48;
                        tick.style.backgroundColor = KidOrange;
                        tick.style.alignItems = Align.Center;
                        tick.style.justifyContent = Justify.Center;
                        Round(tick, 24);
                        tick.Add(new MenuIcon("check", 32, KidText));
                        button.Add(tick);
                    }
                    AddPressFeedback(button);
                }
                else if (n.StartsWith(prefix + ".class."))
                {
                    var classId = n.Substring((prefix + ".class.").Length);
                    StyleBigChip(button, VehicleIcon(classId), classId == selectedClass, 190, 76, 48);
                }
                else if (n.StartsWith(prefix + ".vehicle."))
                {
                    var vehicleId = n.Substring((prefix + ".vehicle.").Length);
                    StyleBigChip(button, VehicleIcon(vehicleId), vehicleId == quickVehicleId, 220, 76, 44);
                }
                else if (n.StartsWith(prefix + ".difficulty."))
                {
                    var diffId = n.Substring((prefix + ".difficulty.").Length);
                    StyleDifficultyChip(button, diffId, diffId == quickDifficulty);
                }
                else if (n == "quick.mode.normal" || n == "quick.mode.elimination")
                {
                    var elimination = n == "quick.mode.elimination";
                    StyleBigChip(button, elimination ? "out" : "flag", elimination == quickElimination, 180, 66, 36);
                }
            }
            // Pair up the small rows so the whole setup fits one screen.
            var classes = root.Q(prefix + ".classes");
            var vehicles = root.Q(prefix + ".vehicles");
            var difficulties = root.Q(prefix + ".difficulties");
            var modes = root.Q(prefix + ".modes");
            HideRowCaptions(difficulties);
            HideRowCaptions(modes);
            PairRows(classes, vehicles, prefix + ".pickRow");
            PairRows(difficulties, modes, prefix + ".optionRow");
            // Nothing in the picker may be squeezed by the scroll content.
            var pickerScroll = root.Q<ScrollView>(prefix + ".scroll");
            if (pickerScroll != null)
                foreach (var child in pickerScroll.contentContainer.Children())
                    child.style.flexShrink = 0;
            foreach (var label in root.Query<Label>().ToList())
                if (label.style.color.value == Deep || ColorDistance(label.style.color.value, Deep) < 0.12f)
                    label.style.color = KidMuted;
            KidStartButton(root.Q<Button>(prefix + ".start"), state);
        }

        private static void PairRows(VisualElement a, VisualElement b, string name)
        {
            if (a == null || a.parent == null) return;
            var parent = a.parent;
            var pair = new VisualElement { name = name };
            pair.style.flexDirection = FlexDirection.Row;
            pair.style.flexWrap = Wrap.NoWrap;
            pair.style.flexShrink = 0;
            pair.style.justifyContent = Justify.Center;
            pair.style.alignItems = Align.Center;
            pair.style.marginTop = 4;
            parent.Insert(parent.IndexOf(a), pair);
            pair.Add(a);
            a.style.flexWrap = Wrap.NoWrap;
            a.style.flexShrink = 0;
            a.style.marginLeft = a.style.marginRight = 18;
            if (b != null)
            {
                pair.Add(b);
                b.style.flexWrap = Wrap.NoWrap;
                b.style.flexShrink = 0;
                b.style.marginLeft = b.style.marginRight = 18;
            }
        }

        private void StyleBigChip(Button button, string icon, bool selected, float width, float height, float iconSize)
        {
            if (button == null) return;
            KidChip(button, icon, selected, iconSize);
            button.style.minWidth = width;
            button.style.height = height;
            button.style.minHeight = height;
            button.style.fontSize = 24;
            Margin(button, 6, 6);
            AddPressFeedback(button);
        }

        private void StyleDifficultyChip(Button button, string difficultyId, bool selected)
        {
            if (button == null) return;
            KidChip(button, null, selected);
            button.style.minWidth = 150;
            button.style.height = 66;
            button.style.minHeight = 66;
            button.style.fontSize = 22;
            Margin(button, 6, 6);
            var rank = DifficultyRank(difficultyId);
            var stars = new VisualElement { pickingMode = PickingMode.Ignore };
            stars.style.flexDirection = FlexDirection.Row;
            for (var i = 0; i < rank; i++)
                stars.Add(new MenuIcon("star", 24, selected ? KidText : KidYellow));
            SideMark(button, stars, rank * 24);
            AddPressFeedback(button);
        }

        private void StyleKidTrialExtras(FoundationState state)
        {
            // Par / record lines become icon + number readouts.
            foreach (var label in root.Query<Label>().ToList())
            {
                if (label.parent == null || label.style.display == DisplayStyle.None) continue;
                if (ColorDistance(label.style.color.value, Seafoam) < 0.12f)
                    label.style.color = KidTeal;
            }
        }

        private void StyleKidCampaign(FoundationState state)
        {
            var title = root.Q<Label>("campaign.title");
            if (title != null)
            {
                title.style.fontSize = 34;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = KidText;
                title.style.marginTop = 12;
            }
            var stars = root.Q<Label>("campaign.stars");
            if (stars != null && state.progression != null && stars.parent != null)
            {
                var pill = new VisualElement { name = "campaign.stars.pill" };
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.style.alignSelf = Align.Center;
                pill.style.backgroundColor = KidSurface;
                Round(pill, 24);
                Pad(pill, 4, 16);
                pill.style.marginBottom = 8;
                stars.parent.Insert(stars.parent.IndexOf(stars), pill);
                pill.Add(new MenuIcon("star", 32, KidYellow));
                stars.text = Ltr(state.progression.campaignStars + "/" + state.progression.campaignRequiredStars);
                stars.style.fontSize = 24;
                stars.style.color = KidText;
                stars.style.unityFontStyleAndWeight = FontStyle.Bold;
                stars.style.marginLeft = 8;
                Margin(stars, 0, 0);
                stars.style.marginLeft = 8;
                pill.Add(stars);
            }

            var worldsStrip = root.Q("campaign.worlds");
            if (worldsStrip != null && state.progression != null
                && state.progression.worlds != null && state.progression.worlds.Count <= 1)
            {
                worldsStrip.style.display = DisplayStyle.None;
                var worldsHeader = root.Q("campaign.worlds.header");
                if (worldsHeader != null) worldsHeader.style.display = DisplayStyle.None;
            }
            var scroll = root.Q<ScrollView>("campaign.scroll");
            var rows = new Dictionary<string, CampaignEventState>();
            if (state.progression != null && state.progression.campaignEvents != null)
                foreach (var row in state.progression.campaignEvents)
                    if (row != null) rows[row.eventId] = row;
            if (scroll != null)
            {
                var grid = new VisualElement { name = "campaign.grid" };
                grid.style.flexDirection = FlexDirection.Row;
                grid.style.flexWrap = Wrap.Wrap;
                grid.style.justifyContent = Justify.Center;
                var eventRows = new List<VisualElement>();
                foreach (var child in scroll.contentContainer.Children())
                    if (child.name != null && child.name.StartsWith("campaign.row."))
                        eventRows.Add(child);
                if (eventRows.Count > 0)
                {
                    scroll.contentContainer.Insert(scroll.contentContainer.IndexOf(eventRows[0]), grid);
                    foreach (var rowEl in eventRows)
                    {
                        grid.Add(rowEl);
                        var eventId = rowEl.name.Substring("campaign.row.".Length);
                        rows.TryGetValue(eventId, out var data);
                        StyleCampaignTile(rowEl, eventId, data, state);
                    }
                }
            }

            var setup = root.Q("campaign.setup");
            if (setup != null)
            {
                setup.style.flexDirection = FlexDirection.Row;
                setup.style.flexWrap = Wrap.Wrap;
                setup.style.justifyContent = Justify.Center;
                setup.style.alignItems = Align.Center;
                setup.style.marginTop = 8;
                HideRowCaptions(root.Q("campaign.difficulties"));
                var reward = root.Q<Label>("campaign.reward");
                if (reward != null)
                {
                    reward.style.color = KidYellow;
                    reward.style.fontSize = 22;
                }
            }
            foreach (var button in root.Query<Button>().ToList())
            {
                var n = button.name ?? "";
                if (n.StartsWith("campaign.vehicle."))
                {
                    var vehicleId = n.Substring("campaign.vehicle.".Length);
                    StyleBigChip(button, VehicleIcon(vehicleId), vehicleId == campaignVehicleId, 200, 70, 40);
                }
                else if (n.StartsWith("campaign.difficulty."))
                {
                    var diffId = n.Substring("campaign.difficulty.".Length);
                    StyleDifficultyChip(button, diffId, diffId == campaignDifficulty);
                    button.style.height = button.style.minHeight = 70;
                }
            }
            var start = root.Q<Button>("campaign.start");
            KidStartButton(start, state);
            if (start != null) { start.style.height = start.style.minHeight = 76; start.style.minWidth = 240; }
            foreach (var label in root.Query<Label>().ToList())
                if (ColorDistance(label.style.color.value, Deep) < 0.12f)
                    label.style.color = KidMuted;
        }

        private void StyleCampaignTile(VisualElement rowEl, string eventId, CampaignEventState data,
            FoundationState state)
        {
            var pick = rowEl.Q<Button>("campaign.event." + eventId);
            var lockLabel = rowEl.Q<Label>("campaign.event." + eventId + ".lock");
            var starLabel = rowEl.Q<Label>("campaign.event." + eventId + ".stars");
            if (pick == null) return;
            var selected = eventId == campaignEventId;
            var locked = data != null && data.locked;
            rowEl.style.flexDirection = FlexDirection.Column;
            rowEl.style.width = StyleKeyword.Auto;
            rowEl.style.backgroundColor = Color.clear;
            Margin(rowEl, 6, 6);
            Pad(rowEl, 0, 0);
            var text = pick.text;
            pick.tooltip = text;
            pick.text = "";
            pick.Clear();
            pick.style.width = 290;
            pick.style.height = 100;
            pick.style.minHeight = 100;
            pick.style.flexDirection = FlexDirection.Row;
            pick.style.alignItems = Align.Center;
            pick.style.backgroundColor = selected ? KidOrange : KidSurface;
            pick.style.overflow = Overflow.Hidden;
            NoBorder(pick);
            Round(pick, 24);
            Pad(pick, 0, 0);
            Margin(pick, 0, 0);
            pick.style.opacity = locked ? 0.5f : 1f;
            var courseId = data != null ? data.courseId : "";
            var art = KidImage(playerHud != null ? playerHud.CoursePreview(courseId) : null, 110, 100, 0);
            art.style.backgroundColor = KidBg;
            if (locked)
            {
                art.style.alignItems = Align.Center;
                art.style.justifyContent = Justify.Center;
                var shade = new VisualElement { pickingMode = PickingMode.Ignore };
                shade.style.position = Position.Absolute;
                shade.style.left = shade.style.right = shade.style.top = shade.style.bottom = 0;
                shade.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
                art.Add(shade);
                art.Add(new MenuIcon("lock", 46, KidText));
            }
            pick.Add(art);
            var info = new VisualElement { pickingMode = PickingMode.Ignore };
            info.style.flexGrow = 1;
            info.style.alignItems = Align.Center;
            info.style.justifyContent = Justify.Center;
            var icons = new VisualElement { pickingMode = PickingMode.Ignore };
            icons.style.flexDirection = FlexDirection.Row;
            icons.style.alignItems = Align.Center;
            icons.Add(new MenuIcon(VehicleIcon(data != null ? data.classId : ""), 44, KidText));
            var kind = new MenuIcon(EventIcon(data != null ? data.eventType : "", data != null && data.finale),
                40, data != null && data.finale ? KidYellow : KidText);
            kind.style.marginLeft = 12;
            icons.Add(kind);
            info.Add(icons);
            KidStars(info, data != null ? data.stars : 0, 28);
            pick.Add(info);
            if (lockLabel != null) lockLabel.style.display = DisplayStyle.None;
            if (starLabel != null) starLabel.style.display = DisplayStyle.None;
            AddPressFeedback(pick);
        }

        private void StyleKidSettings(FoundationState state)
        {
            var title = root.Q<Label>();
            if (title != null && title.parent == root)
            {
                title.text = T("action.settings", state);
                title.style.fontSize = 34;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = KidText;
                title.style.marginTop = 14;
                title.style.marginBottom = 18;
            }
            void Lead(VisualElement control, string icon)
            {
                var row = control != null ? control.parent : null;
                if (row == null || row.Q<MenuIcon>("icon." + icon) != null) return;
                row.style.alignItems = Align.Center;
                row.style.marginTop = row.style.marginBottom = 6;
                foreach (var child in row.Children())
                    if (child is Label caption && caption.name != "status.persistence")
                        caption.style.display = DisplayStyle.None;
                var mark = new MenuIcon(icon, 48, KidText);
                mark.style.marginRight = 16;
                row.Insert(0, mark);
            }
            Lead(langEnButton, "globe");
            Lead(chaseButton, "camera");
            Lead(shakeButton, "steady");
            Lead(musicSlider, "music");
            Lead(effectsSlider, "speaker");
            Lead(voiceSlider, "voice");
            KidChip(langEnButton, null, state.draftProfile != null && state.draftProfile.language == "en");
            KidChip(langHeButton, null, state.draftProfile != null && state.draftProfile.language == "he");
            KidChip(chaseButton, "chase", false, 36);
            KidChip(overheadButton, "overhead", false, 36);
            KidChip(shakeButton, null, false);
            foreach (var slider in new[] { musicSlider, effectsSlider, voiceSlider })
            {
                if (slider == null) continue;
                slider.style.minWidth = 420;
                slider.style.height = 48;
                foreach (var container in slider.Query(className: "unity-base-slider__drag-container").ToList())
                    container.style.height = 48;
                foreach (var tracker in slider.Query(className: "unity-base-slider__tracker").ToList())
                {
                    tracker.style.position = Position.Absolute;
                    tracker.style.top = 19;
                    tracker.style.height = 10;
                    tracker.style.marginTop = 0;
                    tracker.style.backgroundColor = KidSurface;
                    NoBorder(tracker);
                    Round(tracker, 5);
                }
                foreach (var dragger in slider.Query(className: "unity-base-slider__dragger").ToList())
                {
                    dragger.style.width = 40;
                    dragger.style.height = 40;
                    dragger.style.top = 4;
                    dragger.style.marginTop = 0;
                    dragger.style.marginLeft = -20;
                    dragger.style.backgroundColor = KidOrange;
                    NoBorder(dragger);
                    Round(dragger, 20);
                }
            }
            if (saveButton != null)
            {
                NoBorder(saveButton);
                Round(saveButton, 34);
                saveButton.style.backgroundColor = KidGreen;
                saveButton.style.minWidth = 220;
                saveButton.style.height = saveButton.style.minHeight = 76;
                saveButton.style.fontSize = 28;
                IconBeforeText(saveButton, "save", 40, state);
            }
        }

        private void StyleKidBounties(FoundationState state)
        {
            var title = root.Q<Label>("bounties.title");
            if (title != null)
            {
                title.style.fontSize = 34;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = KidText;
                title.style.marginTop = 12;
                var index = root.IndexOf(title);
                if (index + 1 < root.childCount && root[index + 1] is Label hint && string.IsNullOrEmpty(hint.name))
                    hint.style.display = DisplayStyle.None;
            }
            var coins = root.Q<Label>("bounties.coins");
            if (coins != null && coins.parent != null)
            {
                var pill = new VisualElement { name = "bounties.coins.pill" };
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.style.alignSelf = Align.Center;
                pill.style.backgroundColor = KidSurface;
                Round(pill, 24);
                Pad(pill, 4, 16);
                pill.style.marginBottom = 12;
                coins.parent.Insert(coins.parent.IndexOf(coins), pill);
                pill.Add(new MenuIcon("coin", 32, KidYellow));
                coins.text = Ltr((state.savedProfile != null ? state.savedProfile.coins : 0).ToString());
                coins.style.color = KidText;
                coins.style.fontSize = 24;
                coins.style.unityFontStyleAndWeight = FontStyle.Bold;
                coins.style.marginLeft = 8;
                pill.Add(coins);
            }
            var rows = new Dictionary<string, BountyState>();
            if (state.progression != null && state.progression.bounties != null)
                foreach (var row in state.progression.bounties)
                    if (row != null) rows["bounty." + row.assignmentId] = row;
            foreach (var rowEl in root.Children())
            {
                if (rowEl.name == null || !rows.TryGetValue(rowEl.name, out var data)) continue;
                rowEl.style.width = 700;
                rowEl.style.alignSelf = Align.Center;
                rowEl.style.alignItems = Align.Center;
                rowEl.style.justifyContent = Justify.SpaceBetween;
                rowEl.style.flexWrap = Wrap.NoWrap;
                rowEl.style.backgroundColor = KidSurface;
                Round(rowEl, 26);
                Pad(rowEl, 10, 18);
                Margin(rowEl, 6, 0);
                var mark = new MenuIcon(data.completed ? "check" : "star", 48, data.completed ? KidGreen : KidYellow);
                mark.style.marginRight = 14;
                rowEl.Insert(0, mark);
                var name = rowEl.Q<Label>(rowEl.name + ".name");
                if (name != null)
                {
                    name.style.fontSize = 24;
                    name.style.color = KidText;
                    name.style.unityFontStyleAndWeight = FontStyle.Bold;
                    name.style.flexGrow = 1;
                    name.style.unityTextAlign = RootRtl ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
                }
                var progress = rowEl.Q<Label>(rowEl.name + ".progress");
                if (progress != null)
                {
                    progress.style.display = DisplayStyle.None;
                    var track = new VisualElement { name = rowEl.name + ".bar", pickingMode = PickingMode.Ignore };
                    track.style.width = 180;
                    track.style.height = 16;
                    track.style.backgroundColor = KidBg;
                    Round(track, 8);
                    track.style.marginLeft = track.style.marginRight = 12;
                    var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                    var ratio = data.threshold > 0 ? Mathf.Clamp01((float)data.progress / data.threshold) : 0f;
                    fill.style.width = new Length(ratio * 100f, LengthUnit.Percent);
                    fill.style.height = new Length(100, LengthUnit.Percent);
                    fill.style.backgroundColor = KidTeal;
                    Round(fill, 8);
                    track.Add(fill);
                    rowEl.Insert(rowEl.IndexOf(progress), track);
                }
                var reward = rowEl.Q<Label>(rowEl.name + ".reward");
                if (reward != null)
                {
                    var pill = new VisualElement { pickingMode = PickingMode.Ignore };
                    pill.style.flexDirection = FlexDirection.Row;
                    pill.style.alignItems = Align.Center;
                    rowEl.Insert(rowEl.IndexOf(reward), pill);
                    pill.Add(new MenuIcon("coin", 30, KidYellow));
                    reward.text = Ltr("+" + data.rewardCoins);
                    reward.style.fontSize = 24;
                    reward.style.color = KidText;
                    reward.style.unityFontStyleAndWeight = FontStyle.Bold;
                    reward.style.marginLeft = 6;
                    pill.Add(reward);
                }
                var done = rowEl.Q<Label>(rowEl.name + ".done");
                if (done != null) done.style.display = DisplayStyle.None;
            }
        }

        private void StyleKidGarage(FoundationState state)
        {
            if (garagePanel == null) return;
            var title = garagePanel.Q<Label>("garage.title");
            if (title != null)
            {
                title.style.fontSize = 34;
                title.style.unityFontStyleAndWeight = FontStyle.Bold;
                title.style.color = KidText;
                title.style.marginTop = 12;
            }
            var coins = garagePanel.Q<Label>("garage.coins");
            if (coins != null && coins.parent != null && coins.parent.Q("garage.coins.pill") == null)
            {
                var pill = new VisualElement { name = "garage.coins.pill" };
                pill.style.flexDirection = FlexDirection.Row;
                pill.style.alignItems = Align.Center;
                pill.style.alignSelf = Align.Center;
                pill.style.backgroundColor = KidSurface;
                Round(pill, 24);
                Pad(pill, 4, 16);
                pill.style.marginBottom = 10;
                coins.parent.Insert(coins.parent.IndexOf(coins), pill);
                pill.Add(new MenuIcon("coin", 32, KidYellow));
                coins.style.color = KidText;
                coins.style.fontSize = 24;
                coins.style.unityFontStyleAndWeight = FontStyle.Bold;
                coins.style.marginLeft = 8;
                pill.Add(coins);
            }
            var categoryIcons = new Dictionary<string, string>
                { ["acceleration"] = "speed", ["handling"] = "wheel", ["boost"] = "bolt" };
            foreach (var row in garagePanel.Children())
            {
                var n = row.name ?? "";
                if (!n.StartsWith("garage.") || n == "garage.title" || n == "garage.coins") continue;
                var parts = n.Split('.');
                if (parts.Length == 2)
                {
                    // Vehicle header: icon + name, lock if needed.
                    row.style.alignItems = Align.Center;
                    row.style.marginTop = 14;
                    if (row.Q<MenuIcon>() == null)
                        row.Insert(0, new MenuIcon(VehicleIcon(parts[1]), 48, KidText) { style = { marginRight = 12 } });
                    foreach (var label in row.Query<Label>().ToList())
                    {
                        label.style.fontSize = 26;
                        label.style.unityFontStyleAndWeight = FontStyle.Bold;
                        label.style.color = label.name != null && label.name.EndsWith(".locked") ? KidYellow : KidText;
                    }
                }
                else if (parts.Length == 3 && categoryIcons.TryGetValue(parts[2], out var icon))
                {
                    row.style.alignItems = Align.Center;
                    row.style.backgroundColor = KidSurface;
                    Round(row, 22);
                    Pad(row, 6, 14);
                    Margin(row, 4, 0);
                    row.style.width = 640;
                    row.style.alignSelf = Align.Center;
                    row.style.justifyContent = Justify.SpaceBetween;
                    if (row.Q<MenuIcon>("icon." + icon) == null)
                        row.Insert(0, new MenuIcon(icon, 44, KidText) { style = { marginRight = 10 } });
                    string price = null;
                    foreach (var label in row.Query<Label>().ToList())
                    {
                        var ln = label.name ?? "";
                        label.style.fontSize = 22;
                        label.style.unityFontStyleAndWeight = FontStyle.Bold;
                        label.style.color = ln.EndsWith(".effect") ? KidTeal : KidMuted;
                        if (ln.EndsWith(".cost"))
                        {
                            var digits = System.Text.RegularExpressions.Regex.Match(label.text ?? "", "[0-9]+");
                            price = digits.Success ? digits.Value : null;
                            // A maxed upgrade keeps its "max" word; a price
                            // moves onto the buy button.
                            if (price != null) label.style.display = DisplayStyle.None;
                        }
                        if (ln.EndsWith(".level")) label.style.display = DisplayStyle.None;
                    }
                    var installed = row.Q(n + ".installed");
                    if (installed != null)
                    {
                        installed.style.backgroundColor = installed.style.width.value.value > 10 ? KidTeal : KidBg;
                        installed.style.height = 18;
                        Round(installed, 9);
                    }
                    var buy = row.Q<Button>(n + ".buy");
                    if (buy != null)
                    {
                        NoBorder(buy);
                        Round(buy, 26);
                        buy.style.backgroundColor = KidGreen;
                        buy.style.minWidth = 130;
                        buy.style.height = buy.style.minHeight = 56;
                        buy.style.fontSize = 26;
                        if (price != null) buy.text = Ltr(price);
                        IconBeforeText(buy, "coin", 30, state, KidText);
                    }
                }
            }
        }

        private void StyleKidLan(FoundationState state)
        {
            var icons = new Dictionary<string, string>
            {
                ["lan.host"] = "crown", ["lan.discover"] = "search", ["lan.stopSearch"] = "stop",
                ["lan.join"] = "play", ["lan.leave"] = "exit", ["lan.ready"] = "check",
                ["lan.start"] = "play", ["lan.rematch"] = "retry", ["lan.reconnect"] = "wifi",
            };
            foreach (var pair in icons)
            {
                var button = root.Q<Button>(pair.Key);
                if (button == null) continue;
                button.style.minHeight = 70;
                button.style.fontSize = 24;
                Round(button, 30);
                SideMark(button, new MenuIcon(pair.Value, 34, KidText, MirrorIcon(pair.Value, state)), 34);
            }
            var start = root.Q<Button>("lan.start");
            if (start != null) { start.style.backgroundColor = KidGreen; start.style.minWidth = 260; }
            foreach (var label in root.Query<Label>().ToList())
                if (ColorDistance(label.style.color.value, Deep) < 0.12f)
                    label.style.color = KidMuted;
            foreach (var field in root.Query<TextField>().ToList())
            {
                foreach (var input in field.Query(className: "unity-text-field__input").ToList())
                {
                    input.style.backgroundColor = KidSurface;
                    input.style.color = KidText;
                    NoBorder(input);
                    Round(input, 16);
                    input.style.minHeight = 56;
                    input.style.fontSize = 24;
                    Pad(input, 4, 14);
                }
            }
        }

        // In-race overlays: big icon actions, no frames.
        private void StyleKidOverlayButtons(FoundationState state)
        {
            var map = new Dictionary<string, (string icon, Color fill)>
            {
                ["driving.resume"] = ("play", KidGreen),
                ["driving.pauseRetry"] = ("retry", KidSurface),
                ["driving.finishRetry"] = ("retry", KidSurface),
                ["driving.menuExit"] = ("home", KidSurface),
                ["driving.finishExit"] = ("home", KidSurface),
                ["driving.exit"] = ("home", KidSurface),
                ["driving.menuCamera"] = ("camera", KidSurface),
                ["results.continue"] = ("next", KidGreen),
                ["results.next"] = ("next", KidGreen),
                ["results.retry"] = ("retry", KidSurface),
                ["results.again"] = ("retry", KidSurface),
            };
            foreach (var pair in map)
            {
                var button = root.Q<Button>(pair.Key);
                if (button == null) continue;
                // Only buttons inside an overlay panel; the touch deck keeps
                // its own race-control styling.
                if (button.GetFirstAncestorOfType<ScrollView>() == null
                    && button.parent != null && button.parent.name == "hud.controls") continue;
                if (Rtl(state) && button.parent != null)
                {
                    var row = button.parent.name == "kid.wrap" ? button.parent.parent : button.parent;
                    if (row != null && row.style.flexDirection.value == FlexDirection.Row)
                        row.style.flexDirection = FlexDirection.RowReverse;
                }
                NoBorder(button);
                Round(button, 30);
                button.style.backgroundColor = pair.Value.fill;
                button.style.minHeight = 68;
                button.style.minWidth = 180;
                button.style.fontSize = 24;
                IconBeforeText(button, pair.Value.icon, 36, state);
            }
        }
    }
}
