using System.Collections.Generic;
using UnityEngine;

public class Gravity : MonoBehaviour
{
    [SerializeField] private bool _useGravity = true;
    private Rigidbody _rb;
    private float _sqrDistance;
    private Vector3 _vector;
    private List<Transform> _gravityBodies=>GameManager.Instance.GravityBodies;
    private void Start()
    {
        _rb = GetComponent<Rigidbody>();
    }

    private void FixedUpdate()
    {
        if (_useGravity)
        {
            Vector3 acceleration = Vector3.zero;
            foreach (var gravityBody in GameManager.Instance.GravityBodies)
            {
                var _vector = gravityBody.position - _rb.position;
                var _sqrDistance = _vector.sqrMagnitude;
                acceleration += _vector.normalized * (GameManager.Instance.Gravity / _sqrDistance);
            }
            _rb.AddForce(acceleration*Time.fixedDeltaTime, ForceMode.Acceleration);
            Debug.Log("AddForce "+acceleration);
            Debug.DrawRay(_rb.position, _vector, Color.yellow);
        }
    }
}