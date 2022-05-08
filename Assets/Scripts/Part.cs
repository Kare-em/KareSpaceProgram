using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Part : MonoBehaviour
{
    [SerializeField] private float _mass = 1f;
    [SerializeField] private float _maxCollisionSpeed = 1000f;
    public float Mass => _mass;

    private void OnEnable()
    {
        if (!GetComponent<Gravity>())
            gameObject.AddComponent<Gravity>();
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.relativeVelocity.magnitude > _maxCollisionSpeed)
            collision.collider.gameObject.GetComponent<Part>().Separate();
    }

    private void Separate()
    {
        Debug.Log("Separate " + gameObject.name);
        var parentGravity = transform.GetComponentInParent<Gravity>();
        transform.parent = null;
        parentGravity?.UpdateGravity();
        if (!GetComponent<Gravity>())
            gameObject.AddComponent<Gravity>();
    }
}