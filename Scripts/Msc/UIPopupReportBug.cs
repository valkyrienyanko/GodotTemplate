using Godot;
using GodotModules.Netcode.Client;

namespace GodotModules
{
    public class UIPopupReportBug : Node
    {
        [Export] public readonly NodePath NodePathError;
        [Export] public readonly NodePath NodePathDescription;

        private TextEdit Error { get; set; }
        private TextEdit Description { get; set; }

        public override void _Ready()
        {
            Error = GetNode<TextEdit>(NodePathError);
            Description = GetNode<TextEdit>(NodePathDescription);
        }

        private async void _on_Send_pressed()
        {
            if (string.IsNullOrWhiteSpace(Description.Text))
                return;

            await WebClient.PostError(Error.Text, Description.Text);
        }
    }
}