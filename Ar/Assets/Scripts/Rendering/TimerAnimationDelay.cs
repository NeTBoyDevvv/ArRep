using System.Collections;
using UnityEngine;

public class TimerAnimationDelay : MonoBehaviour
{
    void Start()
    {
        StartCoroutine(WaitTenSecondsAndRun());
    }

    IEnumerator WaitTenSecondsAndRun()
    {
        yield return new WaitForSeconds(10); 
        GetComponent<Animator>().SetFloat("Speed", 1.0f);
    }
    void Update()
    {
        
    }
}