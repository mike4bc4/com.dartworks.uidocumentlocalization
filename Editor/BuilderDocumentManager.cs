using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Xml;
using UIDocumentLocalization.Wrappers;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UIDocumentLocalization
{
    [InitializeOnLoad]
    class BuilderDocumentManager
    {
        const float k_SelectionUpdateIntervalSec = 0.1f;
        const float k_VisualTreeAssetUpdateIntervalSec = 0.1f;
        const float k_LocalizationUpdateIntervalSec = 0.1f;

        static BuilderDocumentManager s_Instance;

        VisualTreeAsset m_ActiveVisualTreeAsset;
        VisualElement m_DocumentRootElement;
        Timer m_VisualTreeAssetUpdateTimer;

        Selection m_Selection;
        Timer m_SelectionUpdateTimer;

        Timer m_LocalizationUpdateTimer;

        int m_PreviousContentHash;
        List<string> m_DescendantsGuids;

        public Action m_ReimportCallback;

        public static BuilderDocumentManager instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = new BuilderDocumentManager();
                }

                return s_Instance;
            }
        }

        public static VisualTreeAsset activeVisualTreeAsset => instance.m_ActiveVisualTreeAsset;

        public static VisualElement documentRootElement => instance.m_DocumentRootElement;

        public static bool builderWindowOpened
        {
            get => instance.m_ActiveVisualTreeAsset != null;
        }

        /// <summary>
        /// Called on editor start.
        /// </summary>
        static BuilderDocumentManager()
        {
            s_Instance = new BuilderDocumentManager();
        }

        BuilderDocumentManager()
        {
            m_Selection = new Selection();
            m_DescendantsGuids = new List<string>();
            m_ReimportCallback = OnUxmlImportedAfterSave;

            m_VisualTreeAssetUpdateTimer = new Timer(k_VisualTreeAssetUpdateIntervalSec, true);
            m_VisualTreeAssetUpdateTimer.onTimeout += UpdateActiveVisualTreeAsset;

            m_SelectionUpdateTimer = new Timer(k_SelectionUpdateIntervalSec, true);
            m_SelectionUpdateTimer.onTimeout += UpdateSelection;

            m_LocalizationUpdateTimer = new Timer(k_LocalizationUpdateIntervalSec, true);
            m_LocalizationUpdateTimer.onTimeout += UpdateDocumentLocalization;

            VisualTreeAssetPostprocessor.onUxmlImported += OnUxmlImported;
        }

        void UpdateSelection()
        {
            var builderWindow = BuilderWrapper.activeWindow;
            if (builderWindow == null)
            {
                return;
            }

            var selection = new Selection(builderWindow.selection.selection);
            if (!m_Selection.Equals(selection))
            {
                m_Selection = selection;
                OnSelectionChanged();
            }
        }

        void OnSelectionChanged()
        {
            var database = LocalizationConfigObject.instance.database;
            var localizationWindow = LocalizationWindow.activeWindow;
            if (database == null || localizationWindow == null)
            {
                return;
            }

            localizationWindow.Clear();
            foreach (VisualElement ve in m_Selection)
            {
                if (!ve.TryGetGuid(out string guid, out VisualElement ancestor))
                {
                    continue;
                }

                bool isCustomControlChild = ancestor != null;
                string name = isCustomControlChild ? ve.name : string.Empty;
                if (!database.TryGetEntry(guid, out var entry, name))
                {
                    continue;
                }

                bool isOverride = ve.GetLinkedVisualElementAssetInTemplate() != null;
                var selectedElement = new LocalizationWindowSelectedElement { selectedElementName = ve.name };
                selectedElement.GenerateLocalizedPropertyElements(entry.localizedProperties.Count);
                selectedElement.overrideLabelDisplayed = isOverride;
                localizationWindow.AddSelectedElement(selectedElement);

                var databaseSO = new SerializedObject(database);
                var entrySP = databaseSO.FindProperty($"m_Entries.Array.data[{database.IndexOf(entry)}]");
                if (isOverride)
                {
                    VisualElement overridingElement;
                    overridingElement = ve.GetAncestorDefinedInTreeAsset(m_ActiveVisualTreeAsset);
                    if (overridingElement == null)
                    {
                        continue;
                    }

                    string overridingElementGuid = null;
                    overridingElementGuid = overridingElement.GetStringStylePropertyByName("guid");
                    if (string.IsNullOrEmpty(overridingElementGuid))
                    {
                        continue;
                    }

                    LocalizationData.Override ovr;
                    if (!entry.TryGetOverride(overridingElementGuid, out ovr))
                    {
                        // If override for overriding element does not exist in database, we have to create new one, as something
                        // has to be passed to BindProperty method of visual element.
                        ovr = new LocalizationData.Override()
                        {
                            overridingElementGuid = overridingElementGuid,
                            overridingElementVisualTreeAsset = m_ActiveVisualTreeAsset,
                        };

                        for (int i = 0; i < entry.localizedProperties.Count; i++)
                        {
                            var localizedProperty = new LocalizationData.LocalizedProperty() { name = entry.localizedProperties[i].name };
                            ovr.localizedProperties.Add(localizedProperty);
                        }

                        entry.AddOrReplaceOverride(ovr);

                        // As adding or replacing override in database makes database serialized object outdated, we have to update
                        // such SO and then fetch out serialized properties once again, otherwise we would not be able to get
                        // serialized property of recently added override object.
                        databaseSO.Update();
                        entrySP = databaseSO.FindProperty($"m_Entries.Array.data[{database.IndexOf(entry)}]");
                    }

                    int overrideIndex = entry.overrides.IndexOf(ovr);
                    var overrideSP = entrySP.FindPropertyRelative($"m_Overrides.Array.data[{overrideIndex}]");
                    for (int i = 0; i < entry.localizedProperties.Count; i++)
                    {
                        var localizedPropertyElement = selectedElement.GetLocalizedPropertyElement(i);
                        var localizedPropertySP = entrySP.FindPropertyRelative($"m_LocalizedProperties.Array.data[{i}]");

                        localizedPropertyElement.propertyTextField.BindProperty(localizedPropertySP.FindPropertyRelative("m_Name"));
                        localizedPropertyElement.baseVisualTreeAssetObjectField.BindProperty(entrySP.FindPropertyRelative("m_VisualTreeAsset"));
                        localizedPropertyElement.baseAddressElement.BindProperty(localizedPropertySP.FindPropertyRelative("m_Address"));
                        localizedPropertyElement.baseAddressFoldoutDisplayed = true;

                        var overrideLocalizedPropertySP = overrideSP.FindPropertyRelative($"m_LocalizedProperties.Array.data[{i}]");
                        localizedPropertyElement.addressElement.BindProperty(overrideLocalizedPropertySP.FindPropertyRelative("m_Address"));
                    }
                }
                else
                {
                    for (int i = 0; i < entry.localizedProperties.Count; i++)
                    {
                        var localizedPropertyElement = selectedElement.GetLocalizedPropertyElement(i);
                        var localizedPropertySP = entrySP.FindPropertyRelative($"m_LocalizedProperties.Array.data[{i}]");

                        localizedPropertyElement.propertyTextField.BindProperty(localizedPropertySP.FindPropertyRelative("m_Name"));
                        localizedPropertyElement.addressElement.BindProperty(localizedPropertySP.FindPropertyRelative("m_Address"));
                        localizedPropertyElement.baseAddressFoldoutDisplayed = false;
                    }
                }
            }
        }

        void UpdateActiveVisualTreeAsset()
        {
            var activeVisualTreeAsset = BuilderWrapper.activeWindow?.document.visualTreeAsset;
            if (activeVisualTreeAsset != m_ActiveVisualTreeAsset)
            {
                m_ActiveVisualTreeAsset = activeVisualTreeAsset;
                OnActiveVisualTreeAssetChanged();
            }
        }

        void OnActiveVisualTreeAssetChanged()
        {
            if (builderWindowOpened)
            {
                m_DocumentRootElement = BuilderWrapper.activeWindow.documentRootElement;
                m_PreviousContentHash = m_ActiveVisualTreeAsset.contentHash;
                UpdateDatabase();
                m_Selection.Clear();
                LocalizationWindow.activeWindow.Clear();
            }
        }

        void OnUxmlImported(string path)
        {
            if (!builderWindowOpened || AssetDatabase.GetAssetPath(m_ActiveVisualTreeAsset) != path)
            {
                return;
            }

            m_ReimportCallback?.Invoke();
        }

        void OnUxmlImportedAfterSave()
        {
            AssignOrUpdateGuids();

            // AssignOrUpdateGuids may not trigger reimport, so just make sure it's queued.
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(m_ActiveVisualTreeAsset));
            m_ReimportCallback = OnUxmlImportedAfterGuidsUpdate;
        }

        void OnUxmlImportedAfterGuidsUpdate()
        {
            // Cache selection before contents of document root element are unloaded.
            var cachedSelection = m_Selection.Store(m_DocumentRootElement);

            var activeWindow = BuilderWrapper.activeWindow;

            // First save unsaved changes to clear document dirty flag and avoid dialog popup on load.
            activeWindow.document.activeOpenUXMLFile.SaveUnsavedChanges();

            // Execute full document load to sync visual elements styles with data on disk.
            activeWindow.LoadDocument(m_ActiveVisualTreeAsset);

            // We have to restore selection, because right now it's either empty or contains visual element
            // references from previous builder state (before document reload). Restore is executed with one frame delay
            // (next frame + time until inspectors are updated), to allow builder to refresh after loading.
            var delay = new Delay();
            delay.onTimeout += () => RestoreSelection(cachedSelection);

            // As result of file reimporting, project browser selection might get modified. Here we are 
            // awaiting until end of this frame for such change to happen. If it does, selection is restored.
            ProjectSelectionTracker.RegisterCallback(ProjectSelectionTracker.RestoreSelectionWithoutNotify);
            EditorApplication.delayCall += () => ProjectSelectionTracker.UnregisterCallback(ProjectSelectionTracker.RestoreSelectionWithoutNotify);

            // Now we can update database and remove unused overrides as reading guids will return correct values.
            LocalizationDataManager.UpdateDatabase(m_DocumentRootElement, m_ActiveVisualTreeAsset);
            var previousDescendantsGuids = m_DescendantsGuids;
            m_DescendantsGuids = m_DocumentRootElement.GetDescendantGuids();
            var removedDescendantGuids = GetRemovedDescendantsGuids(previousDescendantsGuids);
            LocalizationDataManager.RemoveUnusedOverrides(removedDescendantGuids);

            // Set reimport callback as default.
            m_ReimportCallback = OnUxmlImportedAfterSave;
        }

        void RestoreSelection(CachedSelection cachedSelection)
        {
            m_Selection.Restore(m_DocumentRootElement, cachedSelection);
            if (m_Selection.Any())
            {
                var ve = m_Selection.Last();
                var activeWindow = BuilderWrapper.activeWindow;
                var viewport = activeWindow.viewport;

                // Add element to selection list.
                activeWindow.selection.Select(viewport, ve);

                // Select element in viewport.
                viewport.SetInnerSelection(ve);

                // Select element in hierarchy explorer.
                activeWindow.hierarchy.UpdateHierarchyAndSelection(false);
            }
        }

        public void UpdateDatabase()
        {
            if (!builderWindowOpened)
            {
                return;
            }

            BuilderWrapper.activeWindow.SaveChanges();

            // Save changes may not trigger asset reimport, so just make sure it's queued.
            AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(m_ActiveVisualTreeAsset));
        }

        List<String> GetRemovedDescendantsGuids(List<string> previousDescendantsGuids)
        {
            var removedDescendantGuids = new List<string>();
            foreach (var guid in previousDescendantsGuids)
            {
                if (m_DescendantsGuids.BinarySearch(guid) < 0)
                {
                    removedDescendantGuids.Add(guid);
                }
            }

            return removedDescendantGuids;
        }

        int AssignOrUpdateGuids()
        {
            var elements = m_DocumentRootElement.GetDescendants();

            List<VisualTreeAsset> visualTreeAssets = new List<VisualTreeAsset>() { m_ActiveVisualTreeAsset };
            visualTreeAssets.AddRange(m_ActiveVisualTreeAsset.templateDependencies);

            int changeCount = 0;
            foreach (var vta in visualTreeAssets)
            {
                string path = AssetDatabase.GetAssetPath(vta);
                if (string.IsNullOrEmpty(path))
                {
                    Debug.LogWarningFormat("Failed to acquire path for asset '{0}'.", vta);
                    continue;
                }

                var document = new XmlDocument();
                try
                {
                    document.Load(path);
                }
                catch (Exception)
                {
                    Debug.LogWarningFormat("Failed to load document at '{0}'.", path);
                    continue;
                }

                foreach (var node in document.GetDescendants())
                {
                    if (node.Name == "ui:UXML" || node.Name == "ui:Template" || node.Name == "Style" || node.Name == "AttributeOverrides")
                    {
                        continue;
                    }

                    if (node.Attributes["style"] != null)
                    {
                        string guid = node.GetInlineStyleProperty("guid")?.Trim('"');
                        if (guid == null || !Guid.TryParse(guid, out var result))
                        {
                            node.SetInlineStyleProperty("guid", $"\"{Guid.NewGuid().ToString("N")}\"");
                            changeCount += 1;
                        }
                    }
                    else
                    {
                        node.SetInlineStyleProperty("guid", $"\"{Guid.NewGuid().ToString("N")}\"");
                        changeCount += 1;
                    }
                }

                try
                {
                    document.Save(path);
                }
                catch (Exception)
                {
                    Debug.LogWarningFormat("Failed to save document at '{0}'.", path);
                }
            }

            AssetDatabase.Refresh();
            return changeCount;
        }

        void UpdateDocumentLocalization()
        {
            if (!builderWindowOpened)
            {
                return;
            }

            var database = LocalizationConfigObject.instance.database;
            if (database == null)
            {
                return;
            }

            var localizableDescendants = m_DocumentRootElement.GetLocalizableDescendants();
            foreach (var localizableDescendant in localizableDescendants)
            {
                if (!localizableDescendant.TryGetGuid(out string guid, out VisualElement ancestor))
                {
                    continue;
                }

                bool isCustomControlChild = ancestor != null;
                string name = isCustomControlChild ? localizableDescendant.name : string.Empty;

                if (!database.TryGetEntry(guid, out var entry, name))
                {
                    continue;
                }

                foreach (var localizedProperty in entry.localizedProperties)
                {
                    var address = localizedProperty.address;
                    var currentAncestor = localizableDescendant.hierarchy.parent;
                    while (currentAncestor != null)
                    {
                        string ancestorGuid = currentAncestor.GetStringStylePropertyByName("guid");
                        if (!string.IsNullOrEmpty(ancestorGuid))
                        {
                            if (entry.TryGetOverride(ancestorGuid, out var ovr) &&
                                ovr.TryGetLocalizedProperty(localizedProperty.name, out var overrideLocalizedProperty) &&
                                !overrideLocalizedProperty.address.isEmpty)
                            {
                                address = overrideLocalizedProperty.address;
                            }
                        }

                        currentAncestor = currentAncestor.hierarchy.parent;
                    }

                    if (!address.isEmpty)
                    {
                        var propertyInfo = localizableDescendant.GetType().GetProperty(localizedProperty.name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        propertyInfo.SetValue(localizableDescendant, address.translation);
                    }
                    else
                    {
                        localizableDescendant.ResetAttribute(localizedProperty.name.ToUxmlAttributeName());
                    }
                }
            }
        }
    }
}
