using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DG.DemiEditor;
using DG.Tweening;
using UnityEditor;
using UnityEngine;

namespace Dott.Editor
{
    [CustomPropertyDrawer(typeof(DOTweenAnimation))]
    public class DOTweenAnimationPropertyDrawer : DOTweenComponentPropertyDrawer<DOTweenAnimation>
    {
        private readonly Dictionary<DOTweenAnimation, IDOTweenAnimation> adapters = new();

        protected override string GetId(DOTweenAnimation component)
        {
            if (!adapters.ContainsKey(component))
            {
                adapters[component] = DottAnimation.FromComponent(component);
            }

            return adapters[component].Label;
        }
    }

    [CustomPropertyDrawer(typeof(IDOTweenAnimation), true)]
    public class DOTweenCallbackPropertyDrawer : DOTweenComponentPropertyDrawer<IDOTweenAnimation>
    {
        protected override string GetId(IDOTweenAnimation component) => component.Label;
    }

    public abstract class DOTweenComponentPropertyDrawer<T> : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var selected = property.objectReferenceValue as Component;
            if (selected == null)
            {
                DrawDefault(position, property, label);
                return;
            }

            var type = selected.GetType();
            var components = selected.GetComponents(type);

            EditorGUI.BeginProperty(position, label, property);

            var controlRect = EditorGUI.PrefixLabel(position, label);
            var halfWidth = controlRect.width / 2;
            const int halfSpacing = 1;

            var popupRect = controlRect.SetWidth(halfWidth - halfSpacing);
            var index = DrawIdPopup(popupRect, components, selected);
            if (index >= 0)
            {
                property.objectReferenceValue = components[index];
            }

            var selectedRect = controlRect;
            selectedRect.xMin += halfWidth + halfSpacing;
            EditorGUI.PropertyField(selectedRect, property, GUIContent.none);

            EditorGUI.EndProperty();

            if (GUI.changed)
            {
                property.serializedObject.ApplyModifiedProperties();
            }
        }

        private int DrawIdPopup(Rect popupRect, Component[] options, Component selected)
        {
            var prefix = options.Length > 1;
            var ids = options.Select((component, i) => prefix ? $"{i}: {GetPopupOption(component)}" : GetPopupOption(component)).ToArray();
            var index = options.IndexOf(selected);
            index = EditorGUI.Popup(popupRect, index, ids);
            return index;
        }

        private string GetPopupOption(Component component)
        {
            if (component is not T typedComponent) { return string.Empty; }

            var id = GetId(typedComponent);
            id = Regex.Replace(id, "/", " ");
            id = Regex.Replace(id, "<[^>]+>", string.Empty);
            return id;
        }

        protected abstract string GetId(T component);

        private static void DrawDefault(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.PropertyField(position, property, label);
            EditorGUI.EndProperty();
        }
    }
}