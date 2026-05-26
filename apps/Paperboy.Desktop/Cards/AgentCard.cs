using System.Windows;

namespace Paperboy.Desktop.Cards;

public static class AgentCard
{
    public static readonly DependencyProperty IdProperty =
        DependencyProperty.RegisterAttached("Id", typeof(string), typeof(AgentCard), new PropertyMetadata(""));

    public static readonly DependencyProperty RoleProperty =
        DependencyProperty.RegisterAttached("Role", typeof(string), typeof(AgentCard), new PropertyMetadata(""));

    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.RegisterAttached("Action", typeof(string), typeof(AgentCard), new PropertyMetadata(""));

    public static void SetId(DependencyObject element, string value) => element.SetValue(IdProperty, value);
    public static string GetId(DependencyObject element) => (string)element.GetValue(IdProperty);

    public static void SetRole(DependencyObject element, string value) => element.SetValue(RoleProperty, value);
    public static string GetRole(DependencyObject element) => (string)element.GetValue(RoleProperty);

    public static void SetAction(DependencyObject element, string value) => element.SetValue(ActionProperty, value);
    public static string GetAction(DependencyObject element) => (string)element.GetValue(ActionProperty);
}
