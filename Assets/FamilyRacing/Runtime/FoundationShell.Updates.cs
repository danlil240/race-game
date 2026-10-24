using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    public sealed partial class FoundationShell
    {
        private ReleaseUpdateChecker releaseUpdates;
        private bool updateDismissed;

        private string UpdateNoticeKey => updateDismissed ? "dismissed" : releaseUpdates?.Offer?.Version ?? "none";

        private void BuildUpdateNotice(Core.Foundation.FoundationState state)
        {
            var offer = releaseUpdates != null ? releaseUpdates.Offer : null;
            // An offer that arrives while racing waits until the player returns to a menu.
            if (offer == null || updateDismissed || (state.screen != "profiles" && state.screen != "sandbox"))
                return;
            var notice = new VisualElement { name = "update.notice" };
            notice.style.position = Position.Absolute;
            notice.style.left = notice.style.right = 12;
            notice.style.bottom = 12;
            notice.style.backgroundColor = Cream;
            notice.style.paddingLeft = notice.style.paddingRight = 12;
            notice.style.paddingTop = notice.style.paddingBottom = 8;
            notice.style.borderTopLeftRadius = notice.style.borderTopRightRadius = 14;
            notice.style.borderBottomLeftRadius = notice.style.borderBottomRightRadius = 14;
            var title = AddLabel(notice, T("update.available", state) + " " + offer.Version +
                "  (" + T("update.installed", state) + " " + Application.version + ")", 22, Deep);
            title.name = "update.title";
            title.style.whiteSpace = WhiteSpace.Normal;
            var row = Row(notice);
            var open = AddButton(row, T("update.open", state), Ocean,
                () => Application.OpenURL(offer.PageUrl), "update.open");
            var later = AddButton(row, T("update.later", state), Deep, () =>
            {
                updateDismissed = true;
                notice.RemoveFromHierarchy();
            }, "update.later");
            open.style.minHeight = later.style.minHeight = 48;
            open.style.fontSize = later.style.fontSize = 20;
            root.Add(notice);
        }
    }
}
