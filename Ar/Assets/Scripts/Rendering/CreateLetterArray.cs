using UnityEditor;
using UnityEngine;

namespace Rendering
{
    public class CreateLetterArray : EditorWindow
    {
        public GameObject letterPrefab;
        public string letters = "ПРИВЕТ МИР";
        public string alphabet = " АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЬЫЪЭЮЯ";
        public float spacing = 0.6f;
    
        [MenuItem("Tools/Create Letter Array")]
        static void ShowWindow()
        {
            GetWindow<CreateLetterArray>("Letter Array Creator");
        }
    
        void OnGUI()
        {
            letterPrefab = (GameObject)EditorGUILayout.ObjectField("Letter Prefab", letterPrefab, typeof(GameObject), false);
            letters = EditorGUILayout.TextField("Letters", letters);
            spacing = EditorGUILayout.FloatField("Spacing", spacing);
        
            if (GUILayout.Button("Create Array") && letterPrefab)
            {
                CreateArray();
            }
        }
    
        void CreateArray()
        {
            GameObject container = new GameObject("LetterArray" );
            container.AddComponent<UpdateLetterArrayParameters>();
            float startX = -(letters.Length - 1) * spacing / 2f;
        
            for (int i = 0; i < letters.Length; i++)
            {
                Vector3 pos = new Vector3(startX + i * spacing, 0, 0);
                GameObject letterObj = (GameObject)PrefabUtility.InstantiatePrefab(letterPrefab, container.transform);
                letterObj.transform.position = pos;
            
                // Set character value
                SetLetterChar(letterObj, letters[i]);
            }
        }
    
        void SetLetterChar(GameObject obj, char c)
        {
            Renderer renderer = obj.GetComponent<Renderer>();
            if (renderer && renderer.sharedMaterial)
            {
                Material mat = new Material(renderer.sharedMaterial);
                renderer.sharedMaterial = mat;
                mat.SetInt("_Value", alphabet.IndexOf(c));
                mat.SetInt("_Seed", Random.Range(0, 100));
            }
        }
    }
}