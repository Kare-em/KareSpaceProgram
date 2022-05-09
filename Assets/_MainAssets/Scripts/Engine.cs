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
    [SerializeField] private float _maxFireVelocityMultiplier = 5f;
    [SerializeField] private float _maxFireEmissionMultiplier = 100f;
    private Rigidbody _rb;
    private MoveController _controller;
    private bool _firePossible;
    private ParticleSystem.MainModule main;
    private ParticleSystem.EmissionModule emission;
    
    public void SetMaxThrust()
    {
        ChangeThrust(1000f);
    }

    public void SetMinThrust()
    {
        ChangeThrust(-1000f);
    }

    public void ChangeThrust(float step)
    {
        _thrustPercent += step;

        if (_thrustPercent > 1f)
            _thrustPercent = 1f;
        else if (_thrustPercent < 0f)
            _thrustPercent = 0f;

        UpdateFire();
    }

    private void UpdateFire()
    {
        if (_thrustPercent > 0f)
        {
            if (!_fire.isPlaying)
                _fire.Play();
        }
        else
        {
            _fire.Stop();
        }

        main.startSpeed = _thrustPercent * _maxFireVelocityMultiplier;
        emission.rateOverTime = _thrustPercent * _maxFireEmissionMultiplier;
    }

    private void Start()
    {
        _rb = GetComponent<Rigidbody>();
        _controller = GetComponentInParent<MoveController>();
        main = _fire.main;
        emission = _fire.emission;
        SetMinThrust();
    }


    private void FixedUpdate()
    {
        if (_ignition)
        {
            _firePossible = _controller.TryExpendFuel(_thrustPercent * _specificFuelConsumption * Time.fixedDeltaTime);
            if (_firePossible)
                _controller.RB.AddForceAtPosition(transform.up * _thrustPercent * _maxForce * Time.fixedDeltaTime,
                    transform.position, ForceMode.Force);
        }
    }
}