using UnityEngine;

public class WorldObjectSpawner : MonoBehaviour
{
    [Header("Настройки")]
    public GameObject prefabToSpawn; 
    public float distance = 1.5f;    

    private GameObject spawnedInstance;
    private Camera mainCam;

    void Start()
    {
        mainCam = Camera.main;
    }

    public void SpawnOrMoveObject()
    {
        if (prefabToSpawn == null) return;

        Vector3 spawnPos = mainCam.transform.position + mainCam.transform.forward * distance;

        Vector3 lookDir = mainCam.transform.forward;
        Quaternion spawnRot = Quaternion.LookRotation(lookDir);

        if (spawnedInstance == null)
        {
            spawnedInstance = Instantiate(prefabToSpawn, spawnPos, spawnRot);
        }
        else
        {
            spawnedInstance.transform.position = spawnPos;
            spawnedInstance.transform.rotation = spawnRot;
        }
    }
}