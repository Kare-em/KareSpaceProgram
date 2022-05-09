using System;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    #region Singleton

    private static GameManager instance;

    public static GameManager Instance
    {
        get
        {
            if (instance == null) instance = FindObjectOfType<GameManager>();
            return instance;
        }
    }

    #endregion

    [SerializeField] private MoveController currentPlayer;
    [SerializeField] private List<Transform> _gravityBodies;
    [SerializeField] private float _gravity;
    public MoveController CurrentPlayer => currentPlayer;

    public List<Transform> GravityBodies => _gravityBodies;

    public float Gravity => _gravity;
}