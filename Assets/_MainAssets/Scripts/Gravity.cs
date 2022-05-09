using System.Collections.Generic;
using UnityEngine;

public class Gravity : MonoBehaviour
{
    [SerializeField] private bool _useGravity = true;
    private Rigidbody _rb;
    private float _sqrDistance;
    private Vector3 _vector;
    private Vector3 acceleration;
    private List<Transform> _gravityBodies => GameManager.Instance.GravityBodies;

    public Rigidbody RB
    {
        get
        {
            if (!_rb)
                _rb = GetComponent<Rigidbody>();
            return _rb;
        }
    }

    public bool UseGravity
    {
        get => _useGravity;
        set => _useGravity = value;
    }

    private void OnEnable()
    {
        UpdateGravity();
    }

    public void DisableGravity() => UseGravity = false;
    public void EnableGravity() => UseGravity = true;

    public void UpdateGravity()
    {
        if (transform.parent)
        {
            Destroy(this);
            return;
        }
        else
        {
            EnableGravity();
            if (!GetComponent<Rigidbody>())
                gameObject.AddComponent<Rigidbody>();
            RB.useGravity = false;
        }

        if (UseGravity)
        {
            RB.mass = 0f;
            foreach (var part in GetComponentsInChildren<Part>())
            {
                RB.mass += part.Mass;
            }
        }
    }

    private void FixedUpdate()
    {
        if (UseGravity)
        {
            acceleration = Vector3.zero;
            foreach (var gravityBody in GameManager.Instance.GravityBodies)
            {
                _vector = gravityBody.position - RB.position;
                _sqrDistance = _vector.sqrMagnitude;
                acceleration += _vector.normalized * (GameManager.Instance.Gravity / _sqrDistance);
            }

            RB.AddForce(acceleration, ForceMode.Acceleration);
            Debug.Log("AddForce " + acceleration);
            Debug.DrawRay(RB.position, _vector, Color.yellow);
        }
    }
}