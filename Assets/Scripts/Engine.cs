using System;
using UnityEngine;
using UnityEngine.Serialization;

public class Engine : MonoBehaviour
{
    [SerializeField] private bool _ignition;
    [SerializeField] private float _maxForce;
    [SerializeField] private float _thrustPercent;
    [SerializeField] private float _specificFuelConsumption; //удельный расход топл
    [SerializeField] private ParticleSystem _fire;
    [SerializeField] private float _maxFireMultiplier = 5f;
    private Rigidbody _rb;
    private ParticleSystem.VelocityOverLifetimeModule emission;
    private MoveController _controller;
    private bool _firePossible;

    public void SetMaxThrust()
    {
        _thrustPercent = 1f;
        UpdateFire();
    }

    public void SetMinThrust()
    {
        _thrustPercent = 0f;

        _fire.Stop();
        UpdateFire();
    }

    public void ChangeThrust(float step)
    {
        _thrustPercent += step;
        if (_thrustPercent > 0)
        {
            if (_thrustPercent > 1)
            {
                SetMaxThrust();
            }
            else
            {
                UpdateFire();
            }
        }
        else
        {
            SetMinThrust();
        }
    }

    private void UpdateFire()
    {
        emission.yMultiplier = _thrustPercent * _maxFireMultiplier;
    }

    private void Start()
    {
        _rb = GetComponent<Rigidbody>();
        _thrustPercent = 0;
        emission = _fire.velocityOverLifetime;
        _controller = GetComponentInParent<MoveController>();
    }


    private void FixedUpdate()
    {
        if (_ignition)
        {
            _firePossible = _controller.TryExpendFuel(_thrustPercent * _specificFuelConsumption * Time.fixedDeltaTime);
            if (_firePossible)
                _controller.RB.AddForceAtPosition(transform.up * _thrustPercent * _maxForce * Time.fixedDeltaTime,transform.position, ForceMode.Force);
        }
    }
}