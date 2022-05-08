using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CosmicBody : MonoBehaviour
{
    [SerializeField] private float _mass=Mathf.Pow(10f,24f);
    
    public float Radius => transform.localScale.x/2;

    public float Mass => _mass;
}
