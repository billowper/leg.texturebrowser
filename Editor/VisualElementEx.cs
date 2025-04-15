using UnityEngine.UIElements;

namespace LowEndGames.TextureBrowser
{
    public static class VisualElementEx
    {
        public static T AddTo<T>(this T element, VisualElement parent)
            where T: VisualElement
        {
            parent.Add(element);
            return element;
        }
        
        public static T WithClasses<T>(this T element, params string[] classNames)
            where T: VisualElement
        {
            foreach (var className in classNames)
            {
                element.AddToClassList(className);
            }
            
            return element;
        }
    }
}