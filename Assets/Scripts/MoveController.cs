using System;
using System.Collections.Generic;
using UnityEngine;

public class MoveController : MonoBehaviour
{
    [Header("Parameters")] [SerializeField]
    private float _torque;

    [Header("Debug")] [SerializeField] private float _lineLength = 5f;
    [SerializeField] private float _timeLength = 50f;

    [Header("Thrust")] private List<Engine> _engines;
    [SerializeField] private float _thrustStep;
    [SerializeField] private float _stepCount = 500f;
    private List<Fuel> _fuelTanks;


    private Rigidbody _rb;
    private bool _forced;

    private void Start()
    {
        _rb = GetComponent<Rigidbody>();
        _engines = new List<Engine>();
        _fuelTanks = new List<Fuel>();
        _engines.AddRange(GetComponentsInChildren<Engine>());
        _fuelTanks.AddRange(GetComponentsInChildren<Fuel>());
    }

    private void FixedUpdate()
    {
        ControlRotation();
    }

    private void Update()
    {
        DrawRay();
        ControlThrust();
    }

    private void DrawRay()
    {
        Debug.DrawLine(transform.position, transform.position + transform.up * _lineLength, Color.green);
        //Debug.DrawLine(transform.position, transform.position+_rb.velocity.normalized * _lineLength, Color.red);

        Vector3 previousVelocity = _rb.velocity;
        Vector3 nextVelocity = previousVelocity;
        Vector3 previousPosition = transform.position;
        Vector3 nextPosition = previousPosition;
        for (int i = 0; i < _stepCount; i++)
        {
            Vector3 acceleration = Vector3.zero;
            foreach (var gravityBody in GameManager.Instance.GravityBodies)
            {
                var _vector = gravityBody.position - previousPosition;
                var _sqrDistance = _vector.sqrMagnitude;
                acceleration += _vector.normalized *
                                (GameManager.Instance.Gravity * Time.fixedDeltaTime / _sqrDistance);
            }

            nextVelocity = previousVelocity + acceleration * _timeLength;
            nextPosition = previousPosition + nextVelocity * _timeLength;

            Debug.DrawLine(previousPosition, nextPosition, Color.cyan);

            previousVelocity = nextVelocity;
            previousPosition = nextPosition;
        }
    }

    public bool TryExpendFuel(float specificFuelConsumption)
    {
        foreach (var fuelTank in _fuelTanks)
        {
            if (fuelTank.ExpendFuel(specificFuelConsumption))
                return true;
        }

        return false;
    }

    private void ControlThrust()
    {
        if (Input.GetKeyDown(KeyCode.LeftShift))
        {
            foreach (var engine in _engines)
            {
                engine.ChangeThrust(_thrustStep);
            }
        }

        if (Input.GetKeyDown(KeyCode.LeftControl))
        {
            foreach (var engine in _engines)
            {
                engine.ChangeThrust(-_thrustStep);
            }
        }

        if (Input.GetKey(KeyCode.Z))
        {
            foreach (var engine in _engines)
            {
                engine.SetMaxThrust();
            }
        }

        if (Input.GetKey(KeyCode.X))
        {
            foreach (var engine in _engines)
            {
                engine.SetMinThrust();
            }
        }
    }

    private void ControlRotation()
    {
        if (Input.GetKey(KeyCode.W))
            _rb.AddRelativeTorque(Vector3.right * _torque, ForceMode.VelocityChange);
        if (Input.GetKey(KeyCode.S))
            _rb.AddRelativeTorque(Vector3.left * _torque, ForceMode.VelocityChange);
        if (Input.GetKey(KeyCode.D))
            _rb.AddRelativeTorque(Vector3.back * _torque, ForceMode.VelocityChange);
        if (Input.GetKey(KeyCode.A))
            _rb.AddRelativeTorque(Vector3.forward * _torque, ForceMode.VelocityChange);
        if (Input.GetKey(KeyCode.Q))
            _rb.AddRelativeTorque(Vector3.up * _torque, ForceMode.VelocityChange);
        if (Input.GetKey(KeyCode.E))
            _rb.AddRelativeTorque(Vector3.down * _torque, ForceMode.VelocityChange);
    }
}