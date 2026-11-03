using System;
using System.Windows;
using System.Windows.Controls;
using Cyclop.App;

namespace Cyclop.Panes;

/// <summary>What a pane may ask of the panel it sits in.</summary>
internal interface IPanelHost
{
    /// <summary>Hands the panel the keyboard, so a field can be typed into.</summary>
    void ClaimKeyboard();
    void Select(Tab tab);
    void Close();
}

/// <summary>
/// One tab's content. Built once and kept — what was typed survives the panel
/// folding — and told when it comes into and goes out of view.
/// </summary>
internal abstract class Pane : Grid
{
    protected readonly AppModel Model;
    protected readonly IPanelHost Host;
    string? header;

    protected Pane(AppModel model, IPanelHost host)
    {
        Model = model;
        Host = host;
        ClipToBounds = true;
    }

    /// <summary>The short text at the right end of the header strip: a
    /// counter, a countdown, the player's name.</summary>
    public string? HeaderText
    {
        get => header;
        protected set
        {
            if (header == value) return;
            header = value;
            HeaderChanged?.Invoke();
        }
    }

    public event Action? HeaderChanged;

    public bool IsShown { get; private set; }

    public void Show()
    {
        IsShown = true;
        Visibility = Visibility.Visible;
        OnShown();
    }

    public void Hide()
    {
        IsShown = false;
        Visibility = Visibility.Collapsed;
        OnHidden();
    }

    protected virtual void OnShown() { }
    protected virtual void OnHidden() { }

    /// <summary>Puts the caret where typing should land once the panel holds
    /// the keyboard.</summary>
    public virtual void FocusField() { }

    /// <summary>Esc inside the pane. True when the pane used it — closed a
    /// picker, cancelled an edit — so the panel does not fold as well.</summary>
    public virtual bool OnEscape() => false;

    /// <summary>The panel folded: privacy reveals are already reset, panes
    /// drop whatever transient state should not greet the next opening.</summary>
    public virtual void OnPanelClosed() { }
}
